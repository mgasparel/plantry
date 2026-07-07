using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Deals.Application;

/// <summary>The per-household outcome of one <see cref="IngestFlyer"/> cycle — for logging + tests.</summary>
/// <param name="Processed">Active subscriptions the worker attempted.</param>
/// <param name="Pulled">Subscriptions whose flyer pulled and parsed (new or refreshed).</param>
/// <param name="Skipped">Byte-identical no-ops (DD5) + subscriptions with nothing to pull.</param>
/// <param name="Failed">Subscriptions whose pull or parse failed (isolated; cycle continued).</param>
/// <param name="PendingCreated">Total deals left <c>Pending</c> across the cycle.</param>
/// <param name="AutoConfirmed">Total deals auto-confirmed from memory (D4).</param>
public sealed record IngestSummary(
    int Processed, int Pulled, int Skipped, int Failed, int PendingCreated, int AutoConfirmed);

/// <summary>
/// DJ2 — the async ingestion pipeline (deals-domain-model §7): for the <b>currently armed household</b>,
/// pull each active <see cref="StoreSubscription"/> → dedup → normalize → match → materialize
/// <see cref="Deal"/>s, auto-confirming remembered matches (via the shared P5-5 <see cref="ConfirmDeal"/>
/// side effects) and queuing the rest for review. The convergence point of the Deals context.
/// <para>
/// <b>Tenancy (security-critical).</b> This service runs inside an already-armed household scope — the
/// background worker sets <see cref="ITenantContext"/> and <c>DealsDbContext.SetHouseholdId</c> per
/// household before resolving it (there is no HTTP principal). It reads only the armed household's
/// RLS-scoped rows and stamps every new aggregate with that household id. A null tenant is a
/// programming error (the worker must arm first) and yields an empty cycle rather than a cross-tenant read.
/// </para>
/// <para>
/// <b>Failure isolation (D1).</b> One bad subscription — a Flipp pull failure, or a parse/materialize
/// error — never aborts the cycle: it is contained and the loop continues to the next subscription. A
/// pull that yields a valid envelope but fails downstream marks its <see cref="FlyerImport"/>
/// <see cref="PullStatus.Failed"/> with <c>error_detail</c> and persists <b>no partial deals</b>; a pull
/// that never returns an envelope (Flipp unreachable) is logged and retried next cycle.
/// </para>
/// <para>
/// <b>Idempotent re-pull (DD5/DD13).</b> A byte-identical re-pull (content-hash match) is a no-op; a
/// changed re-pull updates the same <see cref="FlyerImport"/>, re-stages only still-<c>Pending</c> deals,
/// and freezes <c>Confirmed</c>/<c>Rejected</c> ones.
/// </para>
/// </summary>
public sealed class IngestFlyer(
    IStoreSubscriptionRepository subscriptions,
    IFlyerImportRepository imports,
    IDealRepository deals,
    IDealMatchMemoryRepository memories,
    IFlyerSource source,
    IDealMatcher matcher,
    ICatalogStoreReader stores,
    ICatalogProductReader products,
    ConfirmDeal confirmDeal,
    ITenantContext tenant,
    IClock clock,
    ILogger<IngestFlyer> logger)
{
    public async Task<IngestSummary> RunAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } tenantId)
        {
            logger.LogError("IngestFlyer invoked with no armed tenant — the worker must set TenantContext per household. Skipping.");
            return new IngestSummary(0, 0, 0, 0, 0, 0);
        }

        var household = HouseholdId.From(tenantId);
        var active = await subscriptions.ListActiveAsync(ct);

        int pulled = 0, skipped = 0, failed = 0, pending = 0, autoConfirmed = 0;

        foreach (var sub in active)
        {
            ct.ThrowIfCancellationRequested();

            // Isolate each subscription's unit of work: discard any changes a prior subscription left
            // staged in the shared DealsDbContext but never committed. EF does NOT detach tracked entities
            // when SaveChanges throws, so without this a save-fault in one subscription would strand its
            // Added/Deleted deals in the context and flush them into the NEXT subscription's commit —
            // inserting the failed flyer's deals and deleting a household's prior Pending deals meant only
            // to be replaced (plantry-60p9). Complements the pass-1 stage-throw guard in StageDealsAsync.
            deals.DiscardStagedChanges();

            try
            {
                var outcome = await IngestSubscriptionAsync(household, sub, ct);
                pulled += outcome.Pulled;
                skipped += outcome.Skipped;
                pending += outcome.PendingCreated;
                autoConfirmed += outcome.AutoConfirmed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Failure isolation: one bad subscription never aborts the household's cycle.
                failed++;
                logger.LogError(ex,
                    "IngestFlyer: subscription {SubscriptionId} (store {StoreId}) failed; continuing to the next.",
                    sub.Id.Value, sub.StoreId);
            }
        }

        logger.LogInformation(
            "IngestFlyer cycle for household {HouseholdId}: {Processed} processed, {Pulled} pulled, {Skipped} skipped, {Failed} failed, {Pending} pending, {AutoConfirmed} auto-confirmed.",
            household.Value, active.Count, pulled, skipped, failed, pending, autoConfirmed);

        return new IngestSummary(active.Count, pulled, skipped, failed, pending, autoConfirmed);
    }

    private async Task<IngestSummary> IngestSubscriptionAsync(HouseholdId household, StoreSubscription sub, CancellationToken ct)
    {
        // Resolve the merchant's Flipp id from Catalog (soft-ref → catalog.store). No external ref → nothing to pull.
        var store = await stores.FindAsync(sub.StoreId, ct);
        if (store?.ExternalRef is not { Length: > 0 } externalRef)
        {
            logger.LogWarning("IngestFlyer: store {StoreId} has no external ref; skipping subscription {SubscriptionId}.",
                sub.StoreId, sub.Id.Value);
            return Empty(skipped: 1);
        }

        // ── Pull (untrusted, fragile). Never throws into the caller; a failure is a soft-fail result. ──
        FlyerPullResult pull;
        try
        {
            pull = await source.PullFlyerAsync(externalRef, sub.PostalCode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pull = FlyerPullResult.Failed(ex.Message);
        }

        if (pull.HasError || pull.Window is null || string.IsNullOrWhiteSpace(pull.FlyerExternalId))
        {
            // No persistable envelope (flyer_external_id / window are NOT NULL). Log + retry next cycle.
            logger.LogWarning("IngestFlyer: pull failed for store {StoreId} — {Error}. Retrying next cycle.",
                sub.StoreId, pull.ErrorMessage ?? "empty flyer");
            return Empty(); // not counted as a hard failure — no import row was created
        }

        // DD5 dedup hash: over the CANONICAL deal projection (pull.DedupContent), NOT the verbatim raw
        // payload. Flipp reshuffles items and embeds volatile per-item chrome (impression counters,
        // timestamps) in flyer_items, so hashing RawContent churns daily and re-stages unchanged flyers
        // through the AI matcher; the projection hashes identically when the advertised deals are unchanged
        // (plantry-04ji.4). raw_flyer still stores the verbatim payload below (DD6) — only this input changed.
        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(pull.DedupContent));
        // Parsed-only lookup (plantry-0l05): a Failed-only history returns null, so a prior materialize fault no
        // longer poison-pills this flyer — we fall through to a clean fresh Start below and the Failed rows remain
        // as retained audit. Only a live Parsed envelope routes to the no-op / refresh branches.
        var existing = await imports.FindParsedByDedupKeyAsync(sub.StoreId, pull.FlyerExternalId, ct);

        // ── DD5: byte-identical re-pull is a no-op. ──
        if (existing is not null && existing.ContentHash is not null && existing.ContentHash.AsSpan().SequenceEqual(contentHash))
        {
            sub.RecordPull(pull.FlyerExternalId, clock);
            await subscriptions.SaveChangesAsync(ct);
            return Empty(skipped: 1);
        }

        var result = existing is null
            ? await IngestNewImportAsync(household, sub, pull, contentHash, ct)
            : await RefreshImportAsync(household, sub, existing, pull, contentHash, ct);

        sub.RecordPull(pull.FlyerExternalId, clock);
        await subscriptions.SaveChangesAsync(ct);
        return result;
    }

    private async Task<IngestSummary> IngestNewImportAsync(
        HouseholdId household, StoreSubscription sub, FlyerPullResult pull, byte[] contentHash, CancellationToken ct)
    {
        var import = FlyerImport.Start(household, sub.StoreId, pull.FlyerExternalId, contentHash, pull.Window!, pull.RawContent, clock);

        try
        {
            // Stage every deal in-memory first (untrusted normalize + match). Nothing touches the DB until the
            // whole batch stages cleanly, so a mid-stage parse throw records Failed with zero partial rows.
            var staged = await StageDealsAsync(household, sub, import.Id, pull, frozenNames: null, ct);

            // ── Atomic materialization (plantry-pwkm, DD15) ─────────────────────────────────────────────
            // The envelope, its staged Pending deals, and the Parsed transition commit as ONE unit or not at
            // all. imports + deals wrap the same DealsDbContext, so one transaction spans all three writes; the
            // FlyerImport is INSERTed before its deals because the deal → flyer_import composite FK is enforced
            // yet has no EF navigation (EF cannot order the inserts within a single save). A hard crash between
            // the deal-persist and the Parsed transition rolls the whole write back — no partial FlyerImport row
            // survives to wedge the (household, store, flyer_external_id) dedup key, so the next pull is a clean
            // Start with no unique-index collision.
            await imports.ExecuteInTransactionAsync(async token =>
            {
                // MarkParsed in-memory first, so the envelope INSERTs directly as Parsed (Pulling is now a purely
                // transient in-memory state — no Pulling row is ever written, which is what removes the wedge).
                var mark = import.MarkParsed(staged.PendingCount, clock);
                if (mark.IsFailure)
                    throw new InvalidOperationException($"MarkParsed failed: {mark.Error.Description}");

                await imports.AddAsync(import, token);
                await imports.SaveChangesAsync(token); // INSERT flyer_import (Parsed) — before its deals (composite FK)

                foreach (var s in staged.Deals)
                    await deals.AddAsync(s.Deal, token);
                await deals.SaveChangesAsync(token);   // INSERT deals — same transaction, commits atomically
            }, ct);

            // Cross-context (writes a Pricing observation) and post-persist by design: each deal is already
            // committed Pending, so AutoConfirm loads it by id to flip it, and a single auto-confirm failure
            // simply leaves that deal Pending to resume next cycle — never a wedge (design §2).
            var autoConfirmed = await AutoConfirmAsync(staged, ct);

            return new IngestSummary(0, 1, 0, 0, staged.PendingCount, autoConfirmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A parse/materialize EXCEPTION (not a hard crash): the atomic write rolled back, so record Failed
            // in its OWN short transaction with error_detail (design §3). A hard crash / abort surfaces as
            // OperationCanceledException and is excluded here — nothing is recorded and the flyer is cleanly
            // re-pulled next cycle, with no wedged row to recover.
            await MarkImportFailedAsync(import, ex, ct);
            throw; // surface to the per-subscription isolation boundary (counts as a failed subscription)
        }
    }

    private async Task<IngestSummary> RefreshImportAsync(
        HouseholdId household, StoreSubscription sub, FlyerImport import, FlyerPullResult pull, byte[] contentHash, CancellationToken ct)
    {
        if (import.Status != PullStatus.Parsed)
        {
            // Defensive backstop: FindParsedByDedupKeyAsync only ever returns Parsed rows (plantry-0l05), so this
            // branch is dead unless that lookup regresses. Kept as a guard against a future non-Parsed leak — a
            // Failed-only history is now retried as a fresh Start upstream, not skipped here.
            logger.LogWarning("IngestFlyer: import {ImportId} is {Status}, not Parsed; skipping re-pull refresh.",
                import.Id.Value, import.Status);
            return Empty(skipped: 1);
        }

        var existingDeals = await deals.ListByFlyerImportAsync(import.Id, ct);
        var frozenNames = existingDeals
            .Where(d => d.Status is DealStatus.Confirmed or DealStatus.Rejected)
            .Select(d => d.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);

        // DD13: refresh only still-Pending deals. Stage the flyer's current items FIRST (in-memory, no
        // change-tracker mutation) so a malformed item throws before we touch the shared scoped context —
        // otherwise stranded Deleted entities would leak into the next subscription's SaveChanges and wipe
        // this household's prior Pending deals. Only once staging succeeds do we drop the old Pending deals
        // and add the fresh ones. Items whose normalized name maps to a frozen (resolved) deal are skipped.
        var staged = await StageDealsAsync(household, sub, import.Id, pull, frozenNames, ct);

        // ── Atomic re-pull refresh (plantry-pwkm, DD15) ─────────────────────────────────────────────────
        // Dropping the superseded Pending deals, adding the fresh ones, and the RecordRepull bookkeeping
        // collapse into ONE SaveChangesAsync — EF wraps a single save in one transaction, so a hard crash
        // rolls the whole refresh back to the prior Parsed import with its original Pending deals intact. The
        // pre-atomic split (a deals-save then a separate import-save) was the wedge window this closes. No FK
        // insert ordering is needed — the parent FlyerImport already exists from the original pull.
        foreach (var pendingDeal in existingDeals.Where(d => d.Status == DealStatus.Pending))
            deals.Remove(pendingDeal);
        foreach (var s in staged.Deals)
            await deals.AddAsync(s.Deal, ct);

        var mark = import.RecordRepull(contentHash, pull.Window!, staged.PendingCount, clock);
        if (mark.IsFailure)
            throw new InvalidOperationException($"RecordRepull failed: {mark.Error.Description}");

        await deals.SaveChangesAsync(ct); // DELETE old Pending + INSERT fresh + UPDATE import → one atomic save

        // Post-persist, cross-context (design §2) — see IngestNewImportAsync for the resumability rationale.
        var autoConfirmed = await AutoConfirmAsync(staged, ct);

        return new IngestSummary(0, 1, 0, 0, staged.PendingCount, autoConfirmed);
    }

    /// <summary>
    /// Stage 1 (normalize) + stage 2 (match) for every <see cref="RawDeal"/>, building — but not yet
    /// persisting — one <see cref="Deal"/> each. Remembered matches (D4) are flagged for auto-confirm;
    /// everything else lands <c>Pending</c>. Items whose normalized name is <paramref name="frozenNames"/>
    /// (a resolved deal on a re-pull) are skipped so a resolution is never clobbered (DD13).
    /// </summary>
    private async Task<StagedDeals> StageDealsAsync(
        HouseholdId household, StoreSubscription sub, FlyerImportId importId, FlyerPullResult pull,
        IReadOnlySet<string>? frozenNames, CancellationToken ct)
    {
        // ── Pre-pass (per item): normalize, skip frozen, resolve memory (DD3). A memory hit — positive or
        // negative — resolves the item here and never reaches the AI; only memory-MISSES are queued for the
        // batch matcher, so we send the AI the smallest possible set (plantry-04ji). Order is preserved so
        // the batch result aligns back positionally.
        var items = new List<StagingItem>();
        var toMatch = new List<RawDeal>();          // memory-misses, in order
        var toMatchItems = new List<StagingItem>(); // parallel back-refs into `items`

        foreach (var raw in pull.Deals)
        {
            var normalized = DealNormalizer.Normalize(raw.RawName);

            if (frozenNames is not null && frozenNames.Contains(normalized.Value))
                continue; // resolved deal for this item — frozen, leave it be (DD13)

            var memory = await memories.FindByKeyAsync(sub.StoreId, normalized.Value, ct);
            if (memory is not null)
            {
                var proposal = memory.ProductId is { } remembered
                    ? new MatchProposal(remembered, MatchConfidence.High, "remembered match")
                    : MatchProposal.Unmatched(); // negative memory: "not a tracked product"
                items.Add(new StagingItem(raw, normalized, proposal, memory.ProductId));
                continue;
            }

            var item = new StagingItem(raw, normalized, MatchProposal.Unmatched(), null);
            items.Add(item);
            toMatch.Add(raw);
            toMatchItems.Add(item);
        }

        // ── Batch match the memory-misses in ONE call (the adapter chunks internally, one completion per
        // chunk). Candidates are fetched once — and only when an AI match is actually needed.
        if (toMatch.Count > 0)
        {
            var candidates = await products.ListCandidatesAsync(ct);

            // ── Empty-catalog guard (plantry-04ji.2). With no candidate products the matcher can only ever
            // return Unmatched — every chunk completion would be a guaranteed-'none' AI call (a new or empty
            // catalog otherwise burns one completion per chunk for zero information). Skip the matcher entirely;
            // the memory-miss items already default to MatchProposal.Unmatched() (set at the pre-pass), so they
            // correctly stay Pending. The candidate load stays lazy here — the all-memory-hit path returns
            // before this block and never queries the catalog at all.
            if (candidates.Count > 0)
            {
                var proposals = await matcher.MatchBatchAsync(toMatch, candidates, ct);

                for (var i = 0; i < toMatchItems.Count; i++)
                {
                    // Defensive: the port promises positional alignment, but a misbehaving adapter/fake could
                    // under-fill — a missing position soft-fails to Unmatched rather than throwing.
                    var proposal = i < proposals.Count ? proposals[i] : MatchProposal.Unmatched();

                    // ADR-007 (belt-and-suspenders): an id outside the candidate set is an invention and is
                    // dropped. The real adapter already enforces this per item; this guards test fakes and any
                    // future adapter that doesn't.
                    if (proposal.SuggestedProductId is { } suggested && candidates.All(c => c.Id != suggested))
                    {
                        logger.LogWarning("IngestFlyer: matcher suggested product {ProductId} not in the candidate set; dropping (ADR-007).", suggested);
                        proposal = MatchProposal.Unmatched();
                    }

                    toMatchItems[i].Proposal = proposal;
                }
            }
        }

        // ── Materialize in original flyer order. Remembered matches carry their product id for auto-confirm;
        // everything else lands Pending.
        var staged = new List<StagedDeal>(items.Count);
        var pending = 0;
        foreach (var it in items)
        {
            var deal = Deal.Stage(household, importId, sub.StoreId, it.Raw, it.Normalized, it.Proposal, clock);
            staged.Add(new StagedDeal(deal, it.RememberedProductId));
            if (it.RememberedProductId is null)
                pending++;
        }

        return new StagedDeals(staged, pending);
    }

    /// <summary>
    /// Runs the P5-5 confirm side effects for each remembered match (D4): the deal is already persisted
    /// <c>Pending</c>, so <see cref="ConfirmDeal.AutoConfirmAsync"/> flips it, writes the deal observation,
    /// and refreshes memory — with <c>reviewed_by_user_id = null</c>. A single auto-confirm failure is
    /// isolated: the deal simply stays Pending (resumable), and the cycle proceeds.
    /// </summary>
    private async Task<int> AutoConfirmAsync(StagedDeals staged, CancellationToken ct)
    {
        var confirmed = 0;
        foreach (var s in staged.Deals)
        {
            if (s.RememberedProductId is not { } productId)
                continue;

            var result = await confirmDeal.AutoConfirmAsync(s.Deal.Id, productId, ct);
            if (result.IsSuccess)
                confirmed++;
            else
                logger.LogWarning("IngestFlyer: auto-confirm of deal {DealId} failed ({Error}); it stays Pending.",
                    s.Deal.Id.Value, result.Error.Code);
        }
        return confirmed;
    }

    private async Task MarkImportFailedAsync(FlyerImport import, Exception ex, CancellationToken ct)
    {
        // The atomic materialization rolled back (or never reached the DB), so nothing partial is persisted. The
        // envelope may still be tracked — Unchanged if its INSERT ran inside the now-rolled-back transaction, or
        // Added if the fault struck during that INSERT — and its staged deals tracked Added. Detach the envelope
        // (DiscardStagedChanges intentionally skips Unchanged) and discard the staged deals, then record Failed
        // in its OWN short transaction: a fresh Pulling → Failed envelope built from the same provenance, so the
        // flyer's dedup key is occupied by a terminal Failed row (DD12) exactly as the pre-atomic path left it —
        // never a wedged Pulling row. A fresh Start (not the rolled-back aggregate) keeps the transition valid
        // even when the fault struck after MarkParsed advanced the in-memory status to Parsed; detaching the
        // original first frees its identity on the (household, store, flyer_external_id) dedup key so the fresh
        // envelope inserts without a unique collision (the ValidityWindow is a complex-type value — freely reused).
        imports.Detach(import);
        deals.DiscardStagedChanges();
        try
        {
            var failed = FlyerImport.Start(
                import.HouseholdId, import.StoreId, import.FlyerExternalId, import.ContentHash,
                import.ValidityWindow, import.RawFlyer, clock);
            // Record the ROOT cause: for a DbUpdateException, ex.Message is the useless "error occurred while
            // saving the entity changes" wrapper — GetBaseException() unwraps it to the actual Postgres/driver
            // detail (e.g. the failing constraint), which is what a human needs from error_detail (plantry-cegw).
            var mark = failed.MarkFailed(Truncate(ex.GetBaseException().Message, 1000), clock);
            if (mark.IsFailure)
                return;
            await imports.AddAsync(failed, ct);
            await imports.SaveChangesAsync(ct); // its own transaction — the detach + discard left the context clean
        }
        catch (Exception markEx) when (markEx is not OperationCanceledException)
        {
            logger.LogError(markEx, "IngestFlyer: failed to record a Failed import for store {StoreId}.", import.StoreId);
        }
    }

    private static IngestSummary Empty(int skipped = 0) => new(0, 0, skipped, 0, 0, 0);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record StagedDeal(Deal Deal, Guid? RememberedProductId);

    private sealed record StagedDeals(IReadOnlyList<StagedDeal> Deals, int PendingCount);

    /// <summary>
    /// One non-frozen flyer item as it moves through staging: its raw + normalized form and the resolved
    /// remembered product id (fixed at the pre-pass), plus a <see cref="Proposal"/> that is either set at
    /// the pre-pass (memory hit) or filled in from the batch AI result (memory miss).
    /// </summary>
    private sealed class StagingItem(RawDeal raw, NormalizedName normalized, MatchProposal proposal, Guid? rememberedProductId)
    {
        public RawDeal Raw { get; } = raw;
        public NormalizedName Normalized { get; } = normalized;
        public MatchProposal Proposal { get; set; } = proposal;
        public Guid? RememberedProductId { get; } = rememberedProductId;
    }
}
