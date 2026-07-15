using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that drives the J4 Cook-a-Recipe flow (recipes-domain-model.md §7).
/// <para>
/// Flow (anchor-first, plantry-292b):
/// <list type="number">
/// <item>Opportunistic reconciliation sweep (292c): re-drives any Pending lines from interrupted
/// prior cooks before starting the new cook, so stale Pending lines are cleared at the earliest
/// opportunity without needing a background poller (ADR-010).</item>
/// <item>Applies <c>ServingsScale = desiredServings / recipe.DefaultServings</c> to each ingredient's
/// required quantity.</item>
/// <item>Accepts caller-supplied <see cref="IngredientResolution"/>[] — the Variant Disambiguation
/// Picker output (C7/C11). Each resolution maps one recipe ingredient to one or more variant
/// allocations (<c>variantProductId, quantity, unitId</c>). When no resolution is supplied for an
/// ingredient, default auto-selection (C7) is applied: the ingredient's own product is used with
/// its scaled quantity.</item>
/// <item>Mints the <see cref="CookEvent"/> and adds all planned <see cref="CookConsumeLine"/>
/// children in <see cref="CookConsumeLineStatus.Pending"/> state.</item>
/// <item>Persists the <see cref="CookEvent"/> + its Pending lines in ONE Recipes transaction
/// (the anchor commit) — before any Inventory consume call runs (292b L2).</item>
/// <item>For each Pending line: calls <see cref="IInventoryConsumer.ConsumeAsync"/>; marks the
/// line <see cref="CookConsumeLineStatus.Applied"/> (with any shortfall) on success,
/// <see cref="CookConsumeLineStatus.DeferredUnitGap"/> on <see cref="DeferredUnitGapException"/>
/// (no conversion bridges the unit gap — owed until a conversion lands, plantry-qll2.6), or
/// <see cref="CookConsumeLineStatus.Shorted"/> on <see cref="InvalidOperationException"/>
/// (no-stock). Persists the updated statuses in a second Recipes transaction.</item>
/// <item>Skips untracked staples entirely (C12).</item>
/// <item>Emits <see cref="RecipeCookedEvent"/> after all lines are resolved (§9, O2).</item>
/// </list>
/// </para>
/// </summary>
public sealed class CookRecipe(
    IRecipeRepository recipes,
    ICookEventRepository cookEvents,
    IInventoryConsumer consumer,
    IInventoryProducer producer,
    ICatalogProductReader products,
    RecipeExpansionService expansion,
    IDomainEventDispatcher eventDispatcher,
    IClock clock,
    ITenantContext tenant,
    ReconcilePendingCooks reconciler,
    ApplyDeferredUnitGaps deferredUnitGaps,
    ILogger<CookRecipe> logger)
{
    public async Task<CookRecipeResult> ExecuteAsync(CookRecipeCommand command, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("Cook rejected — no authenticated household.");
            return new CookRecipeResult.Invalid(Error.Unauthorized);
        }
        var household = HouseholdId.From(householdGuid);

        // Opportunistic reconciliation sweep (292c): re-drive any Pending lines from interrupted prior
        // cooks before this one. Best-effort — see TrySweepPendingCooksAsync.
        await TrySweepPendingCooksAsync(ct);

        if (command.DesiredServings < 1)
        {
            logger.LogWarning(
                "Cook rejected for recipe {RecipeId} — invalid servings {DesiredServings}.",
                command.RecipeId.Value, command.DesiredServings);
            return new CookRecipeResult.Invalid(
                Error.Custom("Recipes.InvalidServings", "Desired servings must be at least 1."));
        }

        var recipe = await recipes.GetByIdAsync(command.RecipeId, ct);
        if (recipe is null)
        {
            logger.LogWarning("Cook failed — recipe {RecipeId} not found.", command.RecipeId.Value);
            return new CookRecipeResult.Invalid(Error.NotFound);
        }

        // ── ServingsScale ────────────────────────────────────────────────────────
        var scale = (decimal)command.DesiredServings / recipe.DefaultServings;
        var servingsCooked = command.DesiredServings;

        // ── Expand the recipe to flat product-level lines (D4 choke point) ────────
        // Every consume target is built from the EXPANDED view (recipe-composition.md §6): a flat recipe
        // expands to its own direct ingredients (empty path, factor 1), so behaviour is unchanged; a
        // recipe with inclusions expands to the sub-recipes' products, each pre-scaled by the product of
        // (Inclusion.Servings / sub.DefaultServings) factors along its path. ServingsScale is applied ON
        // TOP of those factors below. Nothing here learns recursion — the expansion service owns it.
        var expandResult = await expansion.ExpandAsync(recipe.Id, ct);
        if (expandResult.IsFailure)
        {
            // Defensive: a missing sub-recipe or an in-memory cycle (N4 should prevent the latter at save).
            logger.LogWarning(
                "Cook failed for recipe {RecipeId} — expansion error {ErrorCode}.",
                command.RecipeId.Value, expandResult.Error.Code);
            return new CookRecipeResult.Invalid(expandResult.Error);
        }
        var expandedLines = expandResult.Value;

        // Ad-hoc added products (plantry-7zjm): existing catalog products the user added to THIS cook
        // via the Cook-page search picker. They participate in the same catalog TrackStock resolution,
        // opportunistic deferred-unit-gap self-heal round-trip, and consume planning as recipe
        // ingredients — no special-casing (the planner materializes them into consume targets).
        var adHocLines = command.AdHocLines ?? [];

        // ── Batch-resolve TrackStock for all candidate product IDs in one round-trip ──
        // The planner (CookConsumePlanner) collects every product id this cook could touch — each
        // expanded line's own product plus every explicit variant allocation (C11) plus every ad-hoc
        // product — applying the same whole-inclusion-skip (D7) and untracked (C12) filtering it uses
        // when planning, so the resolution set and the plan agree. Resolve them all at once via
        // ResolveSummariesAsync (one catalog round-trip) — the planner checks TrackStock from the result.
        var allCandidateIds = CookConsumePlanner.CollectCandidateProductIds(
            expandedLines, command.Resolutions, adHocLines);

        var catalogSummaries = await products.ResolveSummariesAsync(allCandidateIds.ToList(), ct);

        // Opportunistic deferred unit-gap self-heal (plantry-qll2.6): retro-apply any DeferredUnitGap
        // consume lines whose product is in this cook's candidate set. Best-effort — see
        // TryHealDeferredUnitGapsAsync.
        await TryHealDeferredUnitGapsAsync(allCandidateIds, command.RecipeId, ct);

        // ── Mint CookEvent up-front — its Id is the sourceRef on every consume ───
        var cookEventResult = CookEvent.Record(
            recipe.Id, household, servingsCooked, command.UserId, clock);
        if (cookEventResult.IsFailure)
        {
            // Defensive guard: CookEvent.Record only fails on servingsCooked < 1, which
            // DesiredServings is already validated against above — so reaching here means an
            // invariant drifted (or Record grew a new failure mode). Log at Error, not Warning.
            logger.LogError(
                "Cook failed for recipe {RecipeId} — CookEvent.Record rejected unexpectedly: {ErrorCode}.",
                command.RecipeId.Value, cookEventResult.Error.Code);
            return new CookRecipeResult.Invalid(cookEventResult.Error);
        }

        var cookEvent = cookEventResult.Value;

        // ── Plan the consume targets (ONE pure call) ─────────────────────────────
        // The rule matrix — C7 default auto-selection, C9 per-line skip, C11 variant split/swap,
        // C12 untracked/unknown skip, D7 whole-inclusion skip, D8 provenance, plus ad-hoc
        // materialization (plantry-7zjm) — lives in CookConsumePlanner as a pure function of the data
        // resolved above. No IO here; the orchestrator only feeds it what it already loaded.
        var consumeTargets = CookConsumePlanner.Plan(
            expandedLines, command.Resolutions, adHocLines, catalogSummaries, scale);

        // ── Anchor-first: add all consume lines as Pending, then commit (292b) ───
        // Stage the CookEvent and all Pending CookConsumeLines in a single Recipes
        // transaction BEFORE any inventory call runs. If the process dies mid-cook,
        // a reconciler (292c) can detect Pending lines and re-drive them idempotently
        // via the sourceLineRef token (292a).
        foreach (var target in consumeTargets)
            cookEvent.AddConsumeLine(
                target.IngredientId.Value, target.ProductId, target.Quantity, target.UnitId, target.SourceRecipeId);

        // ── Yield-on-cook produce line (plantry-854a, recipe-composition.md §9) ──
        // If the recipe declares a yield AND the user is storing a positive quantity, stage the inventory
        // ADD as a Pending produce line in THIS anchor commit — before the produce call runs — so it is
        // reconcilable on interruption exactly like a consume line. Storing 0 (or a recipe with no yield)
        // adds nothing. The stored amount is denominated in the recipe's declared YieldUnitId.
        var storeYield = recipe is { HasYield: true }
            && command.StoredYieldQuantity > 0m
            && recipe.YieldProductId is { } yieldProduct
            && recipe.YieldUnitId is { } yieldUnit;
        if (storeYield)
            cookEvent.AddProduceLine(
                recipe.YieldProductId!.Value,
                command.StoredYieldQuantity,
                recipe.YieldUnitId!.Value,
                command.StoredYieldExpiry);

        await cookEvents.AddAsync(cookEvent, ct);
        await cookEvents.SaveChangesAsync(ct); // ← anchor commit: CookEvent + Pending consume & produce lines are durable

        // ── Execute consumes; transition each line to Applied or Shorted ─────────
        var lineResults = new List<CookLineResult>();

        foreach (var line in cookEvent.ConsumeLines)
        {
            // Never block on shortfall (C8/R9). ConsumeAsync reports shortfall in the result.
            // ConsumeAsync throws InvalidOperationException when the product has no stock record
            // at all (no lots ever added). Treat that as a Shorted line — cook proceeds and the
            // caller sees a fully-short line rather than an unhandled 500.
            decimal shortfall;
            Guid shortfallUnit;
            try
            {
                var consumeResult = await consumer.ConsumeAsync(
                    line.ProductId,
                    line.Quantity,
                    line.UnitId,
                    ConsumeReason.Recipe,
                    cookEvent.Id.Value,
                    command.UserId,
                    sourceLineRef: line.Id.Value,
                    ct);

                shortfall = consumeResult.ShortfallAmount;
                shortfallUnit = consumeResult.RequestUnitId;
                line.MarkApplied(shortfall);
            }
            catch (DeferredUnitGapException)
            {
                // No conversion bridges the ingredient unit to the product's stock unit (plantry-qll2.6).
                // This is NOT a shortfall — the consume planning pass failed atomically before touching any
                // lot, so the pantry is untouched. Record it as a deferred unit gap: the consume is owed and
                // will be retro-applied automatically when a conversion for the pair lands (never Shorted,
                // which is reserved for a genuine no-stock product and is never retried).
                shortfall = line.Quantity;
                shortfallUnit = line.UnitId;
                line.MarkDeferredUnitGap();
            }
            catch (InvalidOperationException)
            {
                // No stock record for this product — fully short (C8).
                shortfall = line.Quantity;
                shortfallUnit = line.UnitId;
                line.MarkShorted();
            }

            lineResults.Add(new CookLineResult(
                IngredientId.From(line.IngredientId),
                line.ProductId,
                line.Quantity,
                line.UnitId,
                shortfall,
                shortfallUnit));
        }

        // Persist the line status transitions (Applied / Shorted) — second Recipes commit.
        await cookEvents.SaveChangesAsync(ct);

        // ── Execute produces; transition each produce line to Applied or Failed (854a) ──
        // Mirrors the consume loop above: each Pending produce line drives one inventory ADD via
        // IInventoryProducer, idempotent through its sourceLineRef token (the line's own Id). A produce
        // that cannot be recorded (unknown product / cannot hold stock / no location) is marked Failed and
        // the cook proceeds — a stored yield never blocks the cook, mirroring shortfall tolerance (C8/R9).
        if (cookEvent.ProduceLines.Count > 0)
        {
            foreach (var produceLine in cookEvent.ProduceLines)
            {
                try
                {
                    await producer.ProduceAsync(
                        produceLine.ProductId,
                        produceLine.Quantity,
                        produceLine.UnitId,
                        produceLine.ExpiryDate,
                        ProduceReason.Recipe,
                        cookEvent.Id.Value,
                        command.UserId,
                        sourceLineRef: produceLine.Id.Value,
                        ct);

                    produceLine.MarkApplied();
                }
                catch (InvalidOperationException ex)
                {
                    // The yield add could not be recorded — record Failed (terminal) and continue. Logged at
                    // Warning so a systematically failing produce is visible without failing the whole cook.
                    logger.LogWarning(ex,
                        "Yield produce failed for cook {CookEventId}, product {ProductId} — recorded Failed; cook proceeds.",
                        cookEvent.Id.Value, produceLine.ProductId);
                    produceLine.MarkFailed();
                }
            }

            // Persist the produce line status transitions — third Recipes commit.
            await cookEvents.SaveChangesAsync(ct);
        }

        // ── Emit RecipeCooked (§9) ───────────────────────────────────────────────
        // Dispatched post-commit and non-transactionally: if dispatch fails, the CookEvent and
        // consumes are already durable but this side effect is lost. Tolerable only while RecipeCooked
        // has no subscriber — see the guardrail at the handler registration in Program.cs (ADR-014).
        var cookedEvent = new RecipeCookedEvent(
            recipe.Id,
            household,
            servingsCooked,
            command.UserId,
            cookEvent.CookedAt);

        await eventDispatcher.DispatchAsync([cookedEvent], ct);

        DomainTelemetry.RecipesCooked.Add(1);

        var shortedCount = lineResults.Count(l => l.ShortfallAmount == l.RequestedQuantity);
        logger.LogInformation(
            "Recipe cooked. CookEventId: {CookEventId}, RecipeId: {RecipeId}, Servings: {Servings}, Lines: {LineCount}, Shorted: {ShortedCount}, AdHocAdded: {AdHocAdded}.",
            cookEvent.Id.Value, recipe.Id.Value, servingsCooked, lineResults.Count, shortedCount, adHocLines.Count);

        return new CookRecipeResult.Cooked(cookEvent.Id, servingsCooked, lineResults);
    }

    /// <summary>
    /// Opportunistic reconciliation sweep (292c): re-drives any Pending consume lines from interrupted
    /// prior cooks before starting a new cook, so stale Pending lines clear at the earliest opportunity
    /// without a background poller (ADR-010). No-op when nothing is pending. Best-effort — non-cancellation
    /// failures are swallowed so a stuck reconciliation never blocks the new cook.
    /// <see cref="OperationCanceledException"/> propagates: if the request is cancelled there is no point
    /// continuing the new cook either.
    /// </summary>
    private async Task TrySweepPendingCooksAsync(CancellationToken ct)
    {
        try { await reconciler.ExecuteAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* reconciliation is best-effort; do not block the new cook */ }
    }

    /// <summary>
    /// Opportunistic deferred unit-gap self-heal (plantry-qll2.6): retro-applies any DeferredUnitGap consume
    /// lines from prior cooks whose product is in <paramref name="candidateProductIds"/> — a conversion for
    /// the pair may have landed since (AI-seeded or user-entered) without the Composition-layer trigger
    /// firing (e.g. a failed background step). This is the self-healing net (ADR-014 "recoverable from
    /// durable state"): any missed trigger settles on the next cook that touches the product. Best-effort —
    /// a failure here must never block the new cook; <see cref="OperationCanceledException"/> still propagates.
    /// </summary>
    private async Task TryHealDeferredUnitGapsAsync(
        IReadOnlyCollection<Guid> candidateProductIds, RecipeId recipeId, CancellationToken ct)
    {
        if (candidateProductIds.Count == 0)
            return;

        try { await deferredUnitGaps.ExecuteAsync(candidateProductIds, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort: never block the new cook. Logged (not swallowed silently) so a systematically
            // failing self-heal — which would leave stock quietly diverging — is visible to operators.
            logger.LogWarning(ex,
                "Opportunistic deferred unit-gap self-heal failed at cook entry for recipe {RecipeId}; a Composition-layer trigger or the next cook recovers from durable state.",
                recipeId.Value);
        }
    }
}

// ── Command ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Input for <see cref="CookRecipe"/>. <see cref="UserId"/> is the identity of the user initiating
/// the cook — captured from the request principal at the Web layer and passed in explicitly so the
/// application service stays free of ASP.NET / ClaimsPrincipal dependencies (O2).
/// <para>
/// <see cref="Resolutions"/> carries the Variant Disambiguation Picker output (C7/C11): one entry per
/// ingredient the user overrode, swapped, skipped, or split. Ingredients absent from the array are
/// resolved via default auto-selection (C7).
/// </para>
/// </summary>
/// <param name="RecipeId">The recipe being cooked.</param>
/// <param name="DesiredServings">
/// The serving count the user selected; used to compute <c>ServingsScale = desired / default</c>.
/// Must be &gt;= 1.
/// </param>
/// <param name="UserId">
/// Identity of the user who initiated the cook — stamped on <see cref="CookEvent.CookedBy"/>
/// and on each Inventory journal row (O2). Passed explicitly from the Web layer; not read from
/// <c>ITenantContext</c> (which only carries household identity).
/// </param>
/// <param name="Resolutions">
/// Per-ingredient overrides. May be empty (all ingredients use default auto-selection). Ordering
/// within the array does not matter — the service indexes by
/// <see cref="IngredientResolution.IngredientId"/>.
/// </param>
/// <param name="AdHocLines">
/// Existing catalog products the user added to THIS cook via the Cook-page search picker (plantry-7zjm).
/// Each is consumed as part of the cook with no source recipe ingredient (the recipe is untouched).
/// Optional — <c>null</c> or empty means no added products. Kept distinct from
/// <see cref="Resolutions"/> (which is keyed by recipe <see cref="IngredientId"/>) precisely because an
/// added line has no ingredient to key on; it materializes into a <see cref="CookConsumeLine"/> with
/// <see cref="CookConsumeLine.IngredientId"/> = <see cref="Guid.Empty"/>.
/// </param>
/// <param name="StoredYieldQuantity">
/// Yield-on-cook (plantry-854a): how much of the recipe's declared yield product the user is STORING as
/// leftover / prepped stock ("cooked 4, eating 2, storing 2"). Expressed in the recipe's
/// <c>YieldUnitId</c>. Zero (the default) — or a recipe with no yield — adds nothing to inventory. Eaten-now
/// portions need no inventory action.
/// </param>
/// <param name="StoredYieldExpiry">
/// User-supplied use-by date for the stored yield lot (plantry-854a); null for none. Ignored when
/// <paramref name="StoredYieldQuantity"/> is zero or the recipe declares no yield.
/// </param>
public sealed record CookRecipeCommand(
    RecipeId RecipeId,
    int DesiredServings,
    Guid UserId,
    IReadOnlyList<IngredientResolution> Resolutions,
    IReadOnlyList<AdHocLine>? AdHocLines = null,
    decimal StoredYieldQuantity = 0m,
    DateOnly? StoredYieldExpiry = null);

/// <summary>
/// One existing catalog product added to a single cook via the Cook-page search picker (plantry-7zjm).
/// Search-only: the product must already exist in the catalog (no net-new product creation from the
/// Cook page). Consumed as part of the cook (<see cref="ConsumeReason.Recipe"/>, sourceRef → the cook)
/// with no source recipe ingredient. Transient — never persisted as an entity; it becomes a
/// <see cref="CookConsumeLine"/> with a <see cref="Guid.Empty"/> ingredient sentinel.
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3). Must be an existing, tracked product.</param>
/// <param name="Quantity">Quantity to consume in <paramref name="UnitId"/> — must be &gt; 0.</param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3) for <paramref name="Quantity"/>.</param>
public sealed record AdHocLine(
    Guid ProductId,
    decimal Quantity,
    Guid UnitId);

// ── Resolution DTOs ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Cook-time resolution for one recipe ingredient (recipes-domain-model.md §6
/// <c>IngredientResolution</c>, C7/C9/C11).
/// <para>
/// <list type="bullet">
/// <item><see cref="IsSkipped"/> = true: drop the ingredient entirely (C9 skip).</item>
/// <item>One or more <see cref="Allocations"/>: target specific variant products — variant
/// disambiguation (C7/C11) or a swap/modify/add (C9).</item>
/// <item>No allocations and not skipped: falls back to default auto-selection as if no resolution
/// were supplied.</item>
/// </list>
/// </para>
/// <para>Transient — never persisted (§6).</para>
/// <para>
/// With recipe composition (recipe-composition.md §4/§6, D6), a resolution is keyed by the
/// <b>path-qualified identity</b> (<see cref="Path"/>, <see cref="IngredientId"/>): direct ingredients of
/// the cooked recipe use an empty <see cref="Path"/>, so pre-composition call sites and form contracts map
/// 1:1 and current cook behaviour is byte-for-byte preserved. A resolution addressing a line inside an
/// inclusion carries the chain of <see cref="InclusionId"/>s down to that line's owning recipe.
/// A <b>whole-inclusion skip</b> (D7) is created via <see cref="WholeInclusionSkip"/>.
/// </para>
/// </summary>
/// <param name="Path">
/// The chain of <see cref="InclusionId"/>s from the cooked recipe down to the resolved line's owning
/// recipe — empty (or null) for a direct ingredient. Matches <c>ExpandedLine.Path</c>.
/// </param>
public sealed record IngredientResolution(
    IngredientId IngredientId,
    bool IsSkipped,
    IReadOnlyList<VariantAllocation> Allocations,
    IReadOnlyList<InclusionId>? Path = null)
{
    /// <summary>
    /// The '/'-joined GUID serialization of <see cref="Path"/> — an EMPTY string for a direct-line
    /// resolution, matching <c>ExpandedLine.PathKey</c> so the two line up as dictionary keys.
    /// </summary>
    public string PathKey => Path is null || Path.Count == 0
        ? string.Empty
        : string.Join('/', Path.Select(p => p.Value));

    /// <summary>
    /// True when this is a whole-inclusion skip (D7): a skip resolution with no specific ingredient (empty
    /// <see cref="IngredientId"/>) addressing an inclusion path PREFIX. <see cref="CookRecipe"/> drops every
    /// expanded line beneath <see cref="Path"/>.
    /// </summary>
    public bool IsWholeInclusionSkip =>
        IsSkipped && IngredientId.Value == Guid.Empty && Path is { Count: > 0 };

    /// <summary>
    /// Builds a whole-inclusion skip (D7) that drops every expanded line whose path is prefixed by
    /// <paramref name="inclusionPath"/> — "not making the cheese tonight" as one action rather than N
    /// per-line skips. <paramref name="inclusionPath"/> is the chain of <see cref="InclusionId"/>s from the
    /// cooked recipe down to (and including) the inclusion being skipped.
    /// </summary>
    public static IngredientResolution WholeInclusionSkip(IReadOnlyList<InclusionId> inclusionPath) =>
        new(IngredientId.From(Guid.Empty), IsSkipped: true, Allocations: [], Path: inclusionPath);
}

/// <summary>
/// One variant allocation within an <see cref="IngredientResolution"/> — the specific product
/// variant and quantity the user chose for this split (C7/C11, DM-19).
/// </summary>
public sealed record VariantAllocation(
    Guid VariantProductId,
    decimal Quantity,
    Guid UnitId);

// ── Result ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The outcome of a <see cref="CookRecipe.ExecuteAsync"/> call.
/// <see cref="Cooked"/> on success (even when some lines have shortfalls — shortfalls never block
/// the cook, C8/R9); <see cref="Invalid"/> for a validation failure that precluded any DB write.
/// </summary>
public abstract record CookRecipeResult
{
    private CookRecipeResult() { }

    /// <summary>
    /// The cook completed. <see cref="LineResults"/> carries per-line consumed/shortfall data for
    /// the confirmation UI. Lines with <see cref="CookLineResult.HasShortfall"/> = true were
    /// partially satisfied — the cook still proceeded.
    /// </summary>
    public sealed record Cooked(
        CookEventId CookEventId,
        int ServingsCooked,
        IReadOnlyList<CookLineResult> LineResults) : CookRecipeResult;

    /// <summary>Validation failure — no DB write occurred.</summary>
    public sealed record Invalid(Error Error) : CookRecipeResult;
}

/// <summary>
/// Per-line outcome for one consume target — maps an ingredient/variant to the quantity requested
/// and any shortfall that could not be satisfied from the pantry (C8/R9).
/// </summary>
public sealed record CookLineResult(
    IngredientId IngredientId,
    Guid ProductId,
    decimal RequestedQuantity,
    Guid RequestUnitId,
    decimal ShortfallAmount,
    Guid ShortfallUnitId)
{
    public bool HasShortfall => ShortfallAmount > 0m;
}
