using Plantry.Recipes.Application;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Pure, stateless planner that turns a cook's expanded lines, per-line resolutions, and ad-hoc
/// additions into the ordered set of <see cref="ConsumeTarget"/>s to drive against Inventory
/// (recipes-domain-model.md §7). It embodies the cook rule matrix — C7 default auto-selection,
/// C9 per-line skip, C11 variant split/swap, C12 untracked/unknown skip, D7 whole-inclusion skip,
/// D8 provenance — as a function of data the caller has already loaded, issuing <b>zero</b> IO
/// (ADR-021: SQL fetches the data, C# keeps the math). This lets the whole rule matrix be unit-tested
/// directly, without standing up the <see cref="CookRecipe"/> orchestrator and its eight ports.
///
/// <para>
/// Two entry points, split around the single catalog round-trip the orchestrator performs between them:
/// <list type="number">
/// <item><see cref="CollectCandidateProductIds"/> — every product id the cook could possibly touch, so
/// the caller can batch-resolve Catalog <c>track_stock</c> (and run the deferred-unit-gap self-heal)
/// in one round-trip before planning.</item>
/// <item><see cref="Plan"/> — the ordered consume targets, applying the rule matrix against the resolved
/// catalog summaries.</item>
/// </list>
/// Both derive their resolution index and whole-inclusion-skip set from the same raw
/// <see cref="IngredientResolution"/> list, so the two passes agree on which lines are in play.
/// </para>
/// </summary>
public static class CookConsumePlanner
{
    /// <summary>
    /// Collects every product id that could potentially be consumed by this cook — the expanded line's own
    /// product (default auto-selection, C7) plus every variant product id from an explicit allocation
    /// (C11), plus every ad-hoc added product (plantry-7zjm) — so the caller can resolve Catalog
    /// <c>track_stock</c> for all of them in a single round-trip and run the opportunistic deferred-unit-gap
    /// self-heal over the same set. Lines with no quantity/unit (untracked staples, C12) and lines beneath a
    /// whole-inclusion skip (D7) contribute nothing. Ad-hoc product ids are added unconditionally here — the
    /// per-line quantity/unit guard (C12) is applied later in <see cref="Plan"/>, not to the candidate set.
    /// </summary>
    public static IReadOnlyCollection<Guid> CollectCandidateProductIds(
        IReadOnlyList<ExpandedLine> expandedLines,
        IReadOnlyList<IngredientResolution> resolutions,
        IReadOnlyList<AdHocLine> adHocLines)
    {
        var index = ResolutionIndex.Build(resolutions);
        var candidateIds = new HashSet<Guid>();

        foreach (var line in expandedLines)
        {
            if (line.Quantity is null || line.UnitId is null) continue;
            if (index.IsUnderSkippedInclusion(line.Path)) continue;
            candidateIds.Add(line.ProductId);
            if (index.TryGetPerLineResolution(line, out var resolution))
                foreach (var alloc in resolution.Allocations)
                    candidateIds.Add(alloc.VariantProductId);
        }

        foreach (var adHoc in adHocLines)
            candidateIds.Add(adHoc.ProductId);

        return candidateIds;
    }

    /// <summary>
    /// Builds the ordered list of consume targets for a cook from data the caller has already loaded —
    /// no ports, no IO. Applies the cook rule matrix line by line:
    /// <list type="bullet">
    /// <item>C12 — untracked staple (null quantity/unit) is skipped; so is any product absent from
    /// <paramref name="catalogSummaries"/> or with <c>TrackStock = false</c>.</item>
    /// <item>D7 — a line beneath a whole-inclusion skip prefix is dropped entirely.</item>
    /// <item>C9 — an explicit per-line skip resolution drops the line.</item>
    /// <item>C7/C11 — a resolution with allocations targets specific variant products at their chosen
    /// quantities (a split or swap); each untracked/unknown variant is skipped (C12). A resolution with no
    /// allocations and not skipped falls through to default auto-selection.</item>
    /// <item>C7 — otherwise the expanded line's own product is consumed at its scaled quantity.</item>
    /// <item>D8 — provenance is null for a direct line (empty path) and the owning sub-recipe id for a line
    /// pulled in via an inclusion; a variant swap keeps the line's original provenance.</item>
    /// </list>
    /// Ad-hoc added products (plantry-7zjm) are appended last, each with the <see cref="Guid.Empty"/>
    /// ingredient sentinel and no provenance, guarded against non-positive quantity or a missing unit.
    /// </summary>
    /// <param name="expandedLines">The recipe's flat, pre-scaled expanded lines (recipe-composition.md §6).</param>
    /// <param name="resolutions">The Variant Disambiguation Picker output (C7/C9/C11), including any
    /// whole-inclusion skips (D7). May be empty — every line then uses default auto-selection.</param>
    /// <param name="adHocLines">Existing catalog products the user added to this cook. May be empty.</param>
    /// <param name="catalogSummaries">Catalog <c>track_stock</c> facts for the candidate products, resolved
    /// by the caller from <see cref="CollectCandidateProductIds"/> in a single round-trip.</param>
    /// <param name="scale">ServingsScale (<c>desiredServings / defaultServings</c>) applied on top of the
    /// expansion factor already baked into each line's quantity.</param>
    public static IReadOnlyList<ConsumeTarget> Plan(
        IReadOnlyList<ExpandedLine> expandedLines,
        IReadOnlyList<IngredientResolution> resolutions,
        IReadOnlyList<AdHocLine> adHocLines,
        IReadOnlyDictionary<Guid, CatalogProductSummary> catalogSummaries,
        decimal scale)
    {
        var index = ResolutionIndex.Build(resolutions);
        var consumeTargets = new List<ConsumeTarget>();

        foreach (var line in expandedLines)
        {
            // Untracked staple: null Quantity/UnitId means no quantity to consume (C12).
            if (line.Quantity is null || line.UnitId is null)
                continue;

            // Whole-inclusion skip (D7): "not making the cheese tonight" drops every line under the path.
            if (index.IsUnderSkippedInclusion(line.Path))
                continue;

            // ServingsScale on TOP of the expansion factor already baked into line.Quantity (§6).
            var scaledQuantity = line.Quantity.Value * scale;
            var unitId = line.UnitId.Value;
            // Provenance (D8): null for a direct line (empty path — no cross-recipe origin to render),
            // the owning sub-recipe id for a line pulled in via an inclusion.
            var sourceRecipeId = line.Path.Count == 0 ? (Guid?)null : line.SourceRecipeId.Value;

            if (index.TryGetPerLineResolution(line, out var resolution))
            {
                if (resolution.IsSkipped)
                    continue; // explicit per-line skip (C9)

                if (resolution.Allocations.Count > 0)
                {
                    // Explicit variant split or swap (C7/C9/C11).
                    // C12 applies to all paths: skip allocation if variant is untracked or unknown.
                    // Provenance still rides on the expanded line's owning recipe (a swap does not
                    // change which recipe the line belongs to).
                    foreach (var alloc in resolution.Allocations)
                    {
                        if (!catalogSummaries.TryGetValue(alloc.VariantProductId, out var variant) || !variant.TrackStock)
                            continue; // untracked or unknown variant — skip (C12)

                        consumeTargets.Add(new ConsumeTarget(
                            alloc.VariantProductId,
                            alloc.Quantity,
                            alloc.UnitId,
                            line.IngredientId,
                            sourceRecipeId));
                    }
                    continue;
                }
                // Resolution with no allocations and not skipped → fall through to default auto-selection.
            }

            // Default auto-selection (C7): use the expanded line's own product + scaled quantity.
            // Skip if untracked or absent from catalog (C12).
            if (!catalogSummaries.TryGetValue(line.ProductId, out var summary) || !summary.TrackStock)
                continue;

            consumeTargets.Add(new ConsumeTarget(
                line.ProductId,
                scaledQuantity,
                unitId,
                line.IngredientId,
                sourceRecipeId));
        }

        // ── Ad-hoc added products (plantry-7zjm) ──────────────────────────────────
        // Materialize each user-added product into a consume target with NO source recipe ingredient:
        // its IngredientId is the Guid.Empty sentinel ("ad-hoc line — render/label from ProductId").
        // This is a bare soft-ref with no FK to recipe_ingredient (DM-3) and is NOT the idempotency
        // token (that rides on the line's own Id, plantry-fks); the deferred/unit-gap + reconciliation
        // services key on ProductId, never IngredientId — so an ad-hoc line flows through the identical
        // consume path (Pending → Applied/Shorted/DeferredUnitGap) as a recipe ingredient. C12 is
        // applied uniformly: an added product that is untracked or unknown in the catalog is skipped
        // exactly as an untracked recipe ingredient would be (there is no stock to consume). Guards
        // against non-positive quantity or a missing unit so a malformed row never mints a doomed line.
        foreach (var adHoc in adHocLines)
        {
            if (adHoc.Quantity <= 0m || adHoc.UnitId == Guid.Empty)
                continue;
            if (!catalogSummaries.TryGetValue(adHoc.ProductId, out var adHocSummary) || !adHocSummary.TrackStock)
                continue; // untracked or unknown added product — skip (C12)

            consumeTargets.Add(new ConsumeTarget(
                adHoc.ProductId,
                adHoc.Quantity,
                adHoc.UnitId,
                IngredientId.From(Guid.Empty),
                SourceRecipeId: null)); // ad-hoc lines belong to no recipe (D8)
        }

        return consumeTargets;
    }

    /// <summary>
    /// The per-line resolution lookup plus whole-inclusion skip set, derived once from the raw
    /// <see cref="IngredientResolution"/> list (§4/D6). A resolution's key is <c>(PathKey, IngredientId)</c>
    /// — direct lines use an empty path so existing call sites and form contracts map 1:1. A whole-inclusion
    /// skip (D7) is a skip resolution with no specific ingredient addressing an inclusion path PREFIX; it
    /// drops every expanded line beneath that prefix.
    /// </summary>
    private readonly struct ResolutionIndex
    {
        private readonly Dictionary<(string PathKey, Guid IngredientId), IngredientResolution> _perLine;
        private readonly List<IReadOnlyList<InclusionId>> _wholeInclusionSkips;

        private ResolutionIndex(
            Dictionary<(string, Guid), IngredientResolution> perLine,
            List<IReadOnlyList<InclusionId>> wholeInclusionSkips)
        {
            _perLine = perLine;
            _wholeInclusionSkips = wholeInclusionSkips;
        }

        public static ResolutionIndex Build(IReadOnlyList<IngredientResolution> resolutions)
        {
            var perLine = new Dictionary<(string, Guid), IngredientResolution>();
            var wholeInclusionSkips = new List<IReadOnlyList<InclusionId>>();
            foreach (var r in resolutions)
            {
                if (r.IsWholeInclusionSkip)
                    wholeInclusionSkips.Add(r.Path ?? []);
                else
                    perLine[(r.PathKey, r.IngredientId.Value)] = r;
            }
            return new ResolutionIndex(perLine, wholeInclusionSkips);
        }

        public bool TryGetPerLineResolution(ExpandedLine line, out IngredientResolution resolution) =>
            _perLine.TryGetValue((line.PathKey, line.IngredientId.Value), out resolution!);

        /// <summary>
        /// True when <paramref name="linePath"/> lies beneath any whole-inclusion skip prefix (D7) — i.e.
        /// some skip's path is a list-prefix of the line's path. Compared segment-wise on
        /// <see cref="InclusionId"/>s (never on the joined string) so a prefix only matches at inclusion
        /// boundaries.
        /// </summary>
        public bool IsUnderSkippedInclusion(IReadOnlyList<InclusionId> linePath)
        {
            foreach (var prefix in _wholeInclusionSkips)
            {
                if (prefix.Count == 0 || prefix.Count > linePath.Count)
                    continue;
                var match = true;
                for (var i = 0; i < prefix.Count; i++)
                {
                    if (prefix[i] != linePath[i]) { match = false; break; }
                }
                if (match)
                    return true;
            }
            return false;
        }
    }
}

/// <summary>
/// One resolved unit of stock to remove for a cook: a product, a quantity in a unit, the recipe ingredient
/// it satisfies (or the <see cref="Guid.Empty"/> sentinel for an ad-hoc line), and its D8 provenance
/// (the owning sub-recipe id, or null for a direct/ad-hoc line). Produced by
/// <see cref="CookConsumePlanner.Plan"/> and driven one-for-one into <see cref="CookConsumeLine"/>s.
/// </summary>
public readonly record struct ConsumeTarget(
    Guid ProductId,
    decimal Quantity,
    Guid UnitId,
    IngredientId IngredientId,
    Guid? SourceRecipeId);
