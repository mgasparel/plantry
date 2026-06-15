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
/// line <see cref="CookConsumeLineStatus.Applied"/> (with any shortfall) on success, or
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
    ICatalogProductReader products,
    IDomainEventDispatcher eventDispatcher,
    IClock clock,
    ITenantContext tenant,
    ReconcilePendingCooks reconciler)
{
    public async Task<CookRecipeResult> ExecuteAsync(CookRecipeCommand command, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return new CookRecipeResult.Invalid(Error.Unauthorized);
        var household = HouseholdId.From(householdGuid);

        // ── Opportunistic reconciliation (292c) ──────────────────────────────────
        // Sweep the household's Pending consume lines from any interrupted prior cooks before
        // starting a new cook. No-op when there is nothing pending. Non-cancellation failures
        // are swallowed so a stuck reconciliation never blocks the new cook from proceeding.
        // OperationCanceledException propagates: if the request is cancelled, there's no point
        // continuing the new cook either.
        try { await reconciler.ExecuteAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* reconciliation is best-effort; do not block the new cook */ }

        if (command.DesiredServings < 1)
            return new CookRecipeResult.Invalid(
                Error.Custom("Recipes.InvalidServings", "Desired servings must be at least 1."));

        var recipe = await recipes.GetByIdAsync(command.RecipeId, ct);
        if (recipe is null)
            return new CookRecipeResult.Invalid(Error.NotFound);

        // ── ServingsScale ────────────────────────────────────────────────────────
        var scale = (decimal)command.DesiredServings / recipe.DefaultServings;
        var servingsCooked = command.DesiredServings;

        // ── Resolution index keyed by IngredientId ───────────────────────────────
        var resolutionIndex = command.Resolutions
            .ToDictionary(r => r.IngredientId);

        // ── Batch-resolve TrackStock for all candidate product IDs in one round-trip ──
        // Collect every product id that could potentially be consumed: the ingredient's own
        // product (default path) plus every variant product id from explicit allocations.
        // Resolve them all at once via ResolveSummariesAsync — one catalog round-trip for
        // the whole recipe — then check TrackStock from the result dictionary in the loop.
        // C12 is applied uniformly: skip any product absent from the result or with TrackStock=false.
        var allCandidateIds = new HashSet<Guid>();
        foreach (var ingredient in recipe.Ingredients)
        {
            if (ingredient.Quantity is null || ingredient.UnitId is null) continue;
            allCandidateIds.Add(ingredient.ProductId);
            if (resolutionIndex.TryGetValue(ingredient.Id, out var res))
                foreach (var alloc in res.Allocations)
                    allCandidateIds.Add(alloc.VariantProductId);
        }

        var catalogSummaries = await products.ResolveSummariesAsync(allCandidateIds.ToList(), ct);

        // ── Mint CookEvent up-front — its Id is the sourceRef on every consume ───
        var cookEventResult = CookEvent.Record(
            recipe.Id, household, servingsCooked, command.UserId, clock);
        if (cookEventResult.IsFailure)
            return new CookRecipeResult.Invalid(cookEventResult.Error);

        var cookEvent = cookEventResult.Value;

        // ── Build consume targets ─────────────────────────────────────────────────
        var consumeTargets = new List<ConsumeTarget>();

        foreach (var ingredient in recipe.Ingredients)
        {
            // Untracked staple: null Quantity/UnitId means no quantity to consume (C12).
            if (ingredient.Quantity is null || ingredient.UnitId is null)
                continue;

            var scaledQuantity = ingredient.Quantity.Value * scale;
            var unitId = ingredient.UnitId.Value;

            if (resolutionIndex.TryGetValue(ingredient.Id, out var resolution))
            {
                if (resolution.IsSkipped)
                    continue; // explicit skip (C9)

                if (resolution.Allocations.Count > 0)
                {
                    // Explicit variant split or swap (C7/C9/C11).
                    // C12 applies to all paths: skip allocation if variant is untracked or unknown.
                    foreach (var alloc in resolution.Allocations)
                    {
                        if (!catalogSummaries.TryGetValue(alloc.VariantProductId, out var variant) || !variant.TrackStock)
                            continue; // untracked or unknown variant — skip (C12)

                        consumeTargets.Add(new ConsumeTarget(
                            alloc.VariantProductId,
                            alloc.Quantity,
                            alloc.UnitId,
                            ingredient.Id));
                    }
                    continue;
                }
                // Resolution with no allocations and not skipped → fall through to default auto-selection.
            }

            // Default auto-selection (C7): use ingredient's own product + scaled quantity.
            // Skip if untracked or absent from catalog (C12).
            if (!catalogSummaries.TryGetValue(ingredient.ProductId, out var summary) || !summary.TrackStock)
                continue;

            consumeTargets.Add(new ConsumeTarget(
                ingredient.ProductId,
                scaledQuantity,
                unitId,
                ingredient.Id));
        }

        // ── Anchor-first: add all consume lines as Pending, then commit (292b) ───
        // Stage the CookEvent and all Pending CookConsumeLines in a single Recipes
        // transaction BEFORE any inventory call runs. If the process dies mid-cook,
        // a reconciler (292c) can detect Pending lines and re-drive them idempotently
        // via the sourceLineRef token (292a).
        foreach (var target in consumeTargets)
            cookEvent.AddConsumeLine(target.IngredientId.Value, target.ProductId, target.Quantity, target.UnitId);

        await cookEvents.AddAsync(cookEvent, ct);
        await cookEvents.SaveChangesAsync(ct); // ← anchor commit: CookEvent + Pending lines are durable

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

        // ── Emit RecipeCooked (§9) ───────────────────────────────────────────────
        var cookedEvent = new RecipeCookedEvent(
            recipe.Id,
            household,
            servingsCooked,
            command.UserId,
            cookEvent.CookedAt);

        await eventDispatcher.DispatchAsync([cookedEvent], ct);

        return new CookRecipeResult.Cooked(cookEvent.Id, servingsCooked, lineResults);
    }

    private readonly record struct ConsumeTarget(
        Guid ProductId,
        decimal Quantity,
        Guid UnitId,
        IngredientId IngredientId);
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
public sealed record CookRecipeCommand(
    RecipeId RecipeId,
    int DesiredServings,
    Guid UserId,
    IReadOnlyList<IngredientResolution> Resolutions);

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
/// </summary>
public sealed record IngredientResolution(
    IngredientId IngredientId,
    bool IsSkipped,
    IReadOnlyList<VariantAllocation> Allocations);

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
