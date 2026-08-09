using Plantry.Recipes.Application;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Domain service that computes cookability for a recipe at a given serving count
/// (recipes-domain-model.md §7). Stateless; reads live stock via <see cref="IInventoryStockReader"/>
/// and Catalog product facts (specifically <c>track_stock</c> and the parent/variant tree) via
/// <see cref="ICatalogProductReader"/>. Unit conversion for the availability comparison is performed
/// via <see cref="IUnitConverter"/>.
///
/// Rules:
/// <list type="bullet">
///   <item>Untracked staple (<c>track_stock = false</c>): always <see cref="IngredientStatus.Untracked"/> — never Missing/Low (C12).</item>
///   <item>Tracked, parent product (DM-19): sum available stock across ALL variant children before comparing.</item>
///   <item>Tracked, leaf product: compare available vs scaled required (scaled = required × desired / default_servings).</item>
///   <item>InStock when direct-and-DM19 available &gt;= required. When it is short, one-hop substitution
///     edges (plantry-aqpa.1's <see cref="ISubstitutionReader"/>, target = this line's product) top up
///     availability: a substitute's own stock (DM-19 rollup applies to the substitute too, factor 1.0 —
///     not a second substitution hop) converts within its own unit graph to the edge's substitute unit,
///     crosses the edge ratio, then lands in the line's unit via the target product's unit graph. Low/
///     Missing are computed over the combined (direct + substitute) closure: Low when 0 &lt; combined &lt;
///     required, Missing when combined == 0. <see cref="IngredientStatus.InStockViaSubstitute"/> when
///     direct alone was short but the combined closure meets the requirement (plantry-aqpa.2).</item>
///   <item>ExpiresWithinDays is a <b>signed</b> integer set when soonest expiry is within the household's
///     configured "expiring soon" horizon of today (J1/J3): negative = days past use-by (expired); 0 = expires
///     today; positive = days until expiry. The horizon is the single per-household setting owned by Inventory
///     and read through <see cref="IExpiringSoonHorizonReader"/> (plantry-5yhd), so the recipe "use soon" set
///     agrees with the Today expiring-soon widget by construction.</item>
/// </list>
/// </summary>
public sealed class FulfillmentService(
    IInventoryStockReader stockReader,
    ICatalogProductReader catalogReader,
    IUnitConverter unitConverter,
    IExpiringSoonHorizonReader horizonReader,
    ISubstitutionReader substitutionReader)
{
    /// <summary>
    /// Computes the <see cref="FulfillmentResult"/> for <paramref name="recipe"/> at
    /// <paramref name="desiredServings"/>. All stock reads are performed in one batch round-trip.
    /// The "expiring soon" horizon is read once via <see cref="IExpiringSoonHorizonReader"/>.
    /// </summary>
    public async Task<FulfillmentResult> ComputeAsync(
        Recipe recipe,
        int desiredServings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;
        var expiringSoonDays = await horizonReader.GetDaysAsync(ct);

        // Collect all distinct product ids from the ingredient list so we can batch-resolve
        // catalog facts (track_stock, parent/variant tree) and stock snapshots.
        var allProductIds = recipe.Ingredients
            .Select(i => i.ProductId)
            .Distinct()
            .ToList();

        var (catalogById, stockById, substitutionsByTarget) = await ResolveCatalogAndStockAsync(allProductIds, ct);

        // Pre-resolve every unit conversion the pure rule core will need, so the flat computation runs
        // entirely in-memory (ADR-021: async fetches the data up front, the pure core does the math).
        var (converter, unitFactor) = await ResolveConverterAsync(
            recipe.Ingredients.Select(i => (i.ProductId, i.UnitId)), catalogById, stockById, substitutionsByTarget, ct);

        // Delegate to the same pure rule core the sync Compute overload uses (single rule engine).
        return ComputeFlat(
            recipe.Ingredients, scale, today, catalogById, stockById, substitutionsByTarget, converter, unitFactor,
            expiringSoonDays);
    }

    /// <summary>
    /// Expanded-view fulfillment (recipe-composition.md §7, D4): computes availability over the flat
    /// <see cref="EffectiveIngredient"/> set produced by aggregating a recipe's expanded lines by
    /// <c>(ProductId, UnitId)</c>, so a parent's cookability reflects its included recipes' products
    /// (scaled), with duplicate subs (D14) already merged by the caller. Keyed by <c>(ProductId, UnitId)</c>
    /// rather than <see cref="IngredientId"/> because an expanded product has no single owning ingredient.
    /// Shares the exact per-line stock/catalog logic with the flat <see cref="ComputeAsync(Recipe,int,DateOnly,CancellationToken)"/>
    /// path, so a flat recipe (expansion is a no-op) yields identical statuses.
    /// </summary>
    /// <param name="lines">The aggregated effective ingredient set (from <see cref="ExpandedLineAggregation.AggregateByProductAndUnit"/>).</param>
    /// <param name="defaultServings">The recipe's default serving count — the denominator of the scale.</param>
    /// <param name="desiredServings">The serving count availability is evaluated at.</param>
    /// <param name="today">Reference date for expiry-soon classification (J1/J3).</param>
    public async Task<ExpandedFulfillmentResult> ComputeExpandedAsync(
        IReadOnlyList<EffectiveIngredient> lines,
        int defaultServings,
        int desiredServings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / defaultServings;
        var expiringSoonDays = await horizonReader.GetDaysAsync(ct);

        var allProductIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var (catalogById, stockById, substitutionsByTarget) = await ResolveCatalogAndStockAsync(allProductIds, ct);

        // Pre-resolve conversions, then run the shared pure rule core per line — identical logic to the
        // flat path, keyed on product/quantity/unit so a flat recipe yields identical statuses.
        var (converter, unitFactor) = await ResolveConverterAsync(
            lines.Select(l => (l.ProductId, l.UnitId)), catalogById, stockById, substitutionsByTarget, ct);

        var resultLines = new List<ExpandedIngredientFulfillment>(lines.Count);
        foreach (var line in lines)
        {
            var (status, expires, available, unitMismatch, contributingSubstitutes, hasContributingExpiringStock) = ComputeLineCore(
                line.ProductId, line.Quantity, line.UnitId,
                scale, catalogById, stockById, substitutionsByTarget, today, converter, unitFactor, expiringSoonDays);
            resultLines.Add(new ExpandedIngredientFulfillment(
                line.ProductId, line.UnitId, status, expires, available, unitMismatch, contributingSubstitutes,
                hasContributingExpiringStock));
        }

        var overall = BuildOverall(resultLines.Select(l => l.Status));
        return new ExpandedFulfillmentResult(overall, resultLines);
    }

    /// <summary>
    /// Batch-resolves catalog facts (track_stock + parent/variant tree, DM-19), stock snapshots, and
    /// one-hop substitution edges (plantry-aqpa.2) for the given product ids — each in a single
    /// round-trip (one catalog batch, one stock batch, one substitution-edge batch keyed by the
    /// product ids' TARGET side), replacing the former per-product catalog N+1.
    /// </summary>
    private async Task<(
        IReadOnlyDictionary<Guid, CatalogProduct> Catalog,
        IReadOnlyDictionary<Guid, ProductStock> Stock,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>> SubstitutionsByTarget)>
        ResolveCatalogAndStockAsync(IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        // One batch round-trip for edges whose TARGET is one of these products (fulfillment direction).
        var substitutionsByTarget = await substitutionReader.ListByTargetProductIdsAsync(productIds, ct);

        // Substitute products need their own catalog facts + stock too, so the rollup below (and any
        // DM-19 variant expansion of the substitute, factor 1.0 — not a second substitution hop) can
        // draw on them without a further round-trip.
        var substituteProductIds = substitutionsByTarget.Values
            .SelectMany(edges => edges.Select(e => e.SubstituteProductId));
        var allCatalogIds = productIds.Union(substituteProductIds).ToList();

        // One batch round-trip for the catalog facts + variant tree (was one FindAsync per product).
        var catalogById = await catalogReader.FindManyWithVariantsAsync(allCatalogIds, ct);

        // Collect all product ids we actually need stock for: tracked leaf products, and the
        // variant children of any parent-product ingredients or substitutes (DM-19 rollup).
        var stockProductIds = new HashSet<Guid>();
        foreach (var productId in allCatalogIds)
        {
            if (!catalogById.TryGetValue(productId, out var catalogProduct) || !catalogProduct.TrackStock)
                continue; // absent or untracked — no stock query needed

            foreach (var stockRef in StockRefsFor(catalogProduct, productId))
                stockProductIds.Add(stockRef);
        }

        var stockById = stockProductIds.Count > 0
            ? await stockReader.FindStockBatchAsync(stockProductIds.ToList(), ct)
            : new Dictionary<Guid, ProductStock>();

        return (catalogById, stockById, substitutionsByTarget);
    }

    /// <summary>
    /// Pre-resolves every unit conversion the pure rule core will need for the given lines — including
    /// substitution edges (plantry-aqpa.2) — into in-memory lookups, so the core can run fully
    /// synchronously (ADR-021 rule 1: SQL/async fetches the data, C# keeps the math). Conversions are
    /// awaited <b>sequentially</b> — the converter adapter runs over the scoped Catalog EF DbContext,
    /// which forbids concurrent operations on one instance.
    ///
    /// Returns two delegates:
    /// <list type="bullet">
    ///   <item><b>Converter</b>: the existing exact-amount lookup (stock ref, amount, from-unit, to-unit)
    ///     — each distinct (stock ref, from-unit, to-unit) is resolved once, using that ref's actual
    ///     available quantity (repeats across lines are deduplicated). Used both for a line's own/DM-19
    ///     stock and for a substitute's own-unit-graph conversion (both are exact, known-at-load-time
    ///     amounts).</item>
    ///   <item><b>UnitFactor</b>: an amount-independent factor lookup (product id, from-unit, to-unit),
    ///     resolved via a unit-probe conversion of 1. Needed for the substitution edge's target-unit
    ///     landing hop, whose input amount (the substitute's converted, ratio-crossed contribution) is
    ///     only known once the pure core runs the math — unlike every other conversion here, it cannot be
    ///     pre-resolved for a specific amount. Conversion is provably linear/multiplicative
    ///     (<see cref="Plantry.Pantry.Domain.UnitConverter"/>), so factor × amount is exact.</item>
    /// </list>
    /// A stock ref or product whose conversion had no path resolves to a loud failure, so the core treats
    /// it as a zero contribution (partial visibility).
    /// </summary>
    private async Task<(
        Func<Guid, decimal, Guid, Guid, Result<decimal>> Converter,
        Func<Guid, Guid, Guid, Result<decimal>> UnitFactor)> ResolveConverterAsync(
        IEnumerable<(Guid ProductId, Guid? UnitId)> lines,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>> substitutionsByTarget,
        CancellationToken ct)
    {
        // Store a one-unit conversion factor rather than an amount-specific result. Fulfillment allocates
        // individual FEFO lots, so the same product/unit path must be reusable for every lot quantity.
        var resolved = new Dictionary<(Guid StockRef, Guid FromUnit, Guid ToUnit), Result<decimal>>();
        var resolvedFactors = new Dictionary<(Guid ProductId, Guid FromUnit, Guid ToUnit), Result<decimal>>();

        async Task ResolveStockFactorAsync(Guid stockRef, Guid fromUnit, Guid toUnit)
        {
            var key = (stockRef, fromUnit, toUnit);
            if (resolved.ContainsKey(key)) return;
            resolved[key] = await unitConverter.ConvertAsync(stockRef, 1m, fromUnit, toUnit, ct);
        }

        async Task ResolveFactorAsync(Guid productId, Guid fromUnit, Guid toUnit)
        {
            var key = (productId, fromUnit, toUnit);
            if (resolvedFactors.ContainsKey(key)) return;
            resolvedFactors[key] = await unitConverter.ConvertAsync(productId, 1m, fromUnit, toUnit, ct);
        }

        foreach (var (productId, unitId) in lines)
        {
            if (unitId is null) continue; // untracked line — no conversion needed
            if (!catalogById.TryGetValue(productId, out var catalogProduct) || !catalogProduct.TrackStock)
                continue;

            foreach (var stockRef in StockRefsFor(catalogProduct, productId))
            {
                if (!stockById.TryGetValue(stockRef, out var stock)) continue;
                foreach (var sourceUnitId in StockLotsFor(stock).Select(l => l.UnitId).Distinct())
                    await ResolveStockFactorAsync(stockRef, sourceUnitId, unitId.Value);
            }

            // Substitution edges (plantry-aqpa.2) — one hop, no chaining. Pre-resolve each substitute's
            // own-unit-graph conversion (its default unit → the edge's substitute unit, an exact amount
            // since the substitute's available stock is already known) and the target-unit-graph landing
            // factor (the edge's target unit → this line's unit, amount-independent).
            if (substitutionsByTarget.TryGetValue(productId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (!catalogById.TryGetValue(edge.SubstituteProductId, out var substituteCatalogProduct) ||
                        !substituteCatalogProduct.TrackStock)
                        continue; // no substitute stock to draw from

                    foreach (var substStockRef in StockRefsFor(substituteCatalogProduct, edge.SubstituteProductId))
                    {
                        if (!stockById.TryGetValue(substStockRef, out var substStock)) continue;
                        foreach (var sourceUnitId in StockLotsFor(substStock).Select(l => l.UnitId).Distinct())
                            await ResolveStockFactorAsync(substStockRef, sourceUnitId, edge.SubstituteUnitId);
                    }

                    await ResolveFactorAsync(productId, edge.TargetUnitId, unitId.Value);
                }
            }
        }

        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter = (stockRef, amount, fromUnit, toUnit) =>
        {
            var factor = resolved.GetValueOrDefault((stockRef, fromUnit, toUnit), ConversionUnavailable);
            return factor.IsSuccess
                ? Result<decimal>.Success(amount * factor.Value)
                : factor;
        };

        Func<Guid, Guid, Guid, Result<decimal>> unitFactor = (productId, fromUnit, toUnit) =>
            resolvedFactors.GetValueOrDefault((productId, fromUnit, toUnit), ConversionUnavailable);

        return (converter, unitFactor);
    }

    private static readonly Result<decimal> ConversionUnavailable =
        Result<decimal>.Failure(Error.Custom("Catalog.NoConversionPath", "No conversion path."));

    /// <summary>
    /// The stock product ids a line draws availability from: a leaf product draws from itself; a parent
    /// product (DM-19) draws from each of its live variant children. Single source of truth for "which
    /// stock feeds this line", shared by stock batching, conversion pre-resolution, and the rule core.
    /// </summary>
    private static IReadOnlyList<Guid> StockRefsFor(CatalogProduct catalogProduct, Guid productId) =>
        catalogProduct.IsParent ? catalogProduct.VariantProductIds : [productId];

    /// <summary>
    /// Returns the active lots exposed by the Recipes stock-read seam. Older callers and test doubles
    /// may still provide only the aggregate snapshot, so that shape is represented as one synthetic lot.
    /// Sorting here keeps the pure rule core defensive if an adapter supplies facts that are not already
    /// FEFO ordered; the stable sort preserves the adapter's deterministic order for equal expiries.
    /// </summary>
    private static IReadOnlyList<ActiveStockLot> StockLotsFor(ProductStock stock)
    {
        var lots = stock.ActiveLots?
            .Where(l => l.AvailableQuantity > 0m)
            .OrderBy(l => l.ExpiryDate is null)
            .ThenBy(l => l.ExpiryDate ?? DateOnly.MaxValue)
            .ToList();

        if (lots is { Count: > 0 })
            return lots;

        return stock.AvailableQuantity > 0m
            ? [new ActiveStockLot(stock.AvailableQuantity, stock.DefaultUnitId, stock.SoonestExpiry)]
            : [];
    }

    /// <summary>
    /// Pure overload: computes the <see cref="FulfillmentResult"/> for <paramref name="recipe"/>
    /// at <paramref name="desiredServings"/> using <b>only</b> the data already loaded by the caller.
    /// Issues zero further round-trips (ADR-021 rule 1: SQL fetches data, C# keeps the math).
    ///
    /// The <paramref name="converter"/> delegate must resolve quantities between units without
    /// any IO — it is the caller's responsibility to have pre-loaded units and product conversions.
    /// On conversion failure the variant contributes zero (same partial-visibility rule as the
    /// async path).
    /// </summary>
    /// <param name="recipe">The recipe to evaluate.</param>
    /// <param name="desiredServings">Target serving count (may differ from <c>recipe.DefaultServings</c>).</param>
    /// <param name="today">Reference date for expiry-soon classification (J1/J3).</param>
    /// <param name="catalogById">Pre-loaded product facts keyed by product id — must include all
    /// distinct product ids referenced by <paramref name="recipe"/> plus variant children of any
    /// parent product, and — when <paramref name="substitutionsByTarget"/> is non-empty — every
    /// substitute product plus its variant children too.</param>
    /// <param name="stockById">Pre-loaded stock snapshots keyed by product id — includes variant
    /// children (and substitute products' own variant children); products with no active stock are
    /// absent (treated as zero).</param>
    /// <param name="substitutionsByTarget">One-hop substitution edges (plantry-aqpa.1/aqpa.2) keyed by
    /// TARGET product id — a product id absent from this dictionary has no substitutes. Pass an empty
    /// dictionary for a caller that does not support substitution.</param>
    /// <param name="converter">Sync unit conversion delegate: (productId, amount, fromUnitId, toUnitId) → Result.</param>
    /// <param name="unitFactor">Sync amount-independent conversion-factor delegate: (productId, fromUnitId,
    /// toUnitId) → Result. Used only for a substitution edge's target-unit landing hop (plantry-aqpa.2);
    /// unused when <paramref name="substitutionsByTarget"/> is empty.</param>
    /// <param name="expiringSoonDays">The household's "expiring soon" horizon in days — the caller reads it
    /// once via <see cref="IExpiringSoonHorizonReader"/> and passes it in (ADR-021: the pure overload does no IO).</param>
    public FulfillmentResult Compute(
        Recipe recipe,
        int desiredServings,
        DateOnly today,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>> substitutionsByTarget,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        Func<Guid, Guid, Guid, Result<decimal>> unitFactor,
        int expiringSoonDays)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;
        // Same pure rule core as the async ComputeAsync path — the caller having pre-loaded the data is
        // the only difference (MealPlanning borrows pre-computed enrichment facts, ADR-021).
        return ComputeFlat(
            recipe.Ingredients, scale, today, catalogById, stockById, substitutionsByTarget, converter, unitFactor,
            expiringSoonDays);
    }

    /// <summary>
    /// Maps a recipe's flat ingredient set through the shared per-line rule core into a
    /// <see cref="FulfillmentResult"/>. Shared verbatim by <see cref="ComputeAsync(Recipe,int,DateOnly,CancellationToken)"/>
    /// (which pre-resolves the converter over live ports) and the pure <see cref="Compute"/> overload
    /// (which is handed a ready converter) — so both paths are byte-identical.
    /// </summary>
    private static FulfillmentResult ComputeFlat(
        IReadOnlyList<Ingredient> ingredients,
        decimal scale,
        DateOnly today,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>> substitutionsByTarget,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        Func<Guid, Guid, Guid, Result<decimal>> unitFactor,
        int expiringSoonDays)
    {
        var lines = new List<IngredientFulfillment>(ingredients.Count);
        foreach (var ingredient in ingredients)
        {
            var (status, expires, available, unitMismatch, contributingSubstitutes, hasContributingExpiringStock) = ComputeLineCore(
                ingredient.ProductId, ingredient.Quantity, ingredient.UnitId,
                scale, catalogById, stockById, substitutionsByTarget, today, converter, unitFactor, expiringSoonDays);
            lines.Add(new IngredientFulfillment(
                ingredient.Id, status, expires, available, unitMismatch, contributingSubstitutes,
                hasContributingExpiringStock));
        }

        return new FulfillmentResult(BuildOverall(lines.Select(l => l.Status)), lines);
    }

    /// <summary>
    /// The single pure cookability rule engine — the one place the status rules live (C12 untracked, R5
    /// defensive null qty/unit, DM-19 parent/variant stock rollup, unit-conversion comparison, signed
    /// J1/J3 expiry-soon horizon). Keyed only on product/quantity/unit so it is agnostic to whether the
    /// line came from a direct ingredient (flat) or an aggregated expanded line, and to whether the
    /// converter is live (async path, pre-resolved) or caller-supplied (pure overload). Returns the
    /// availability status, the signed expiry-soon days (or null), the available quantity in the
    /// line's unit (or null when nothing is available / the line is untracked), and a display-only
    /// <c>UnitMismatch</c> flag — true when the line reads as Missing <b>only</b> because real on-hand
    /// stock (quantity &gt; 0) could not be converted to the recipe unit, so the pantry cannot be
    /// compared rather than being genuinely empty (plantry-z2sr). The flag never alters the status or
    /// the cookability rollup — it exists purely so the UI can distinguish "can't compare units" from
    /// "not in pantry". Does no IO.
    /// </summary>
    private static (
        IngredientStatus Status,
        int? ExpiresWithinDays,
        decimal? AvailableQuantity,
        bool UnitMismatch,
        IReadOnlyList<Guid> ContributingSubstituteProductIds,
        bool HasContributingExpiringStock) ComputeLineCore(
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubstitutionEdge>> substitutionsByTarget,
        DateOnly today,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        Func<Guid, Guid, Guid, Result<decimal>> unitFactor,
        int expiringSoonDays)
    {
        // Unresolvable product → Missing.
        if (!catalogById.TryGetValue(productId, out var catalogProduct))
            return (IngredientStatus.Missing, null, null, false, [], false);

        // Untracked staple (track_stock = false) is always satisfied (C12) — and, defensively, a null
        // quantity/unit ("to taste") is treated the same even on a tracked product (R5).
        if (!catalogProduct.TrackStock || quantity is null || unitId is null)
            return (IngredientStatus.Untracked, null, null, false, [], false);

        var scaledRequired = quantity.Value * scale;

        // Roll up available stock (in the line's unit) and soonest expiry across the line's stock refs:
        // a leaf draws from itself; a parent (DM-19) sums across its live variant children. Keep each
        // converted lot so the waste signal can mirror the actual FEFO quantity consumed instead of
        // treating one aggregate quantity plus one earliest expiry as evidence for the whole line.
        decimal totalAvailableInLineUnit = 0m;
        DateOnly? soonestExpiry = null;
        var directLots = new List<ConvertedStockLot>();
        // True when some ref holds real stock (qty > 0) that could not be converted to the line unit —
        // the "can't compare" signal behind the display-only UnitMismatch flag (plantry-z2sr).
        var hadUnconvertibleStock = false;
        foreach (var stockRef in StockRefsFor(catalogProduct, productId))
        {
            if (!stockById.TryGetValue(stockRef, out var stock))
                continue; // no stock record → contributes 0

            if (stock.SoonestExpiry is { } expiry &&
                (soonestExpiry is null || expiry < soonestExpiry.Value))
                soonestExpiry = expiry;

            foreach (var lot in StockLotsFor(stock))
            {
                if (lot.ExpiryDate is { } lotExpiry &&
                    (soonestExpiry is null || lotExpiry < soonestExpiry.Value))
                    soonestExpiry = lotExpiry;

                var converted = converter(stockRef, lot.AvailableQuantity, lot.UnitId, unitId.Value);
                if (converted.IsSuccess && converted.Value > 0m)
                {
                    totalAvailableInLineUnit += converted.Value;
                    directLots.Add(new ConvertedStockLot(converted.Value, lot.ExpiryDate));
                }
                else if (!converted.IsSuccess && lot.AvailableQuantity > 0m)
                {
                    hadUnconvertibleStock = true;
                }
                // On conversion failure the lot contributes 0 — partial visibility is better than a crash.
            }
        }

        var directAvailable = totalAvailableInLineUnit;
        var directAllocation = AllocateLots(directLots, Math.Min(directAvailable, scaledRequired), today, expiringSoonDays);

        // Substitution (plantry-aqpa.2), pursued only when direct stock (incl. DM-19 rollup) is short —
        // one hop, no chaining: apply every edge whose TARGET is this line's product.
        var substituteContribution = 0m;
        var remainingSubstituteRequirement = Math.Max(0m, scaledRequired - directAvailable);
        var hasContributingExpiringStock = directAllocation.HasContributingExpiringStock;
        // Display-only (plantry-aqpa.5): the substitute product ids that actually landed a positive
        // contribution toward this line — surfaced to the UI so a InStockViaSubstitute row can name
        // which product closed the gap, without claiming a precise per-substitute split (the pantry
        // touchpoint is display-only; it does not attempt to reproduce the exact deduction math the
        // Cook page's C11 picker computes at consume time).
        var contributingSubstitutes = new List<Guid>();
        if (directAvailable < scaledRequired &&
            substitutionsByTarget.TryGetValue(productId, out var edges))
        {
            foreach (var edge in edges)
            {
                if (!catalogById.TryGetValue(edge.SubstituteProductId, out var substituteCatalogProduct) ||
                    !substituteCatalogProduct.TrackStock)
                    continue; // no substitute stock to draw from

                // Convert each substitute lot independently — DM-19 rollup applies to the substitute too
                // (factor 1.0; not a second substitution hop). Keeping the lot expiry attached lets the
                // allocation below identify whether this edge actually supplies a use-soon quantity.
                var substituteLots = new List<ConvertedStockLot>();
                foreach (var substStockRef in StockRefsFor(substituteCatalogProduct, edge.SubstituteProductId))
                {
                    if (!stockById.TryGetValue(substStockRef, out var substStock))
                        continue;

                    // Expiry-soon (J1/J3): every contributing substitute stock ref feeds the soonest
                    // expiry too, same as variant rollup.
                    if (substStock.SoonestExpiry is { } substExpiry &&
                        (soonestExpiry is null || substExpiry < soonestExpiry.Value))
                        soonestExpiry = substExpiry;

                    foreach (var lot in StockLotsFor(substStock))
                    {
                        if (lot.ExpiryDate is { } lotExpiry &&
                            (soonestExpiry is null || lotExpiry < soonestExpiry.Value))
                            soonestExpiry = lotExpiry;

                        var convertedSubstStock = converter(
                            substStockRef, lot.AvailableQuantity, lot.UnitId, edge.SubstituteUnitId);
                        if (convertedSubstStock.IsSuccess && convertedSubstStock.Value > 0m)
                        {
                            substituteLots.Add(new ConvertedStockLot(convertedSubstStock.Value, lot.ExpiryDate));
                        }
                        // Conversion failure on a substitute path contributes zero — same partial-visibility
                        // rule as the line's own stock.
                    }
                }

                var substituteQtyInSubstituteUnit = substituteLots.Sum(l => l.QuantityInTargetUnit);
                if (substituteQtyInSubstituteUnit <= 0m)
                    continue;

                if (edge.SubstituteQuantity <= 0m)
                    continue;

                // Land in the line's unit via the target product's own unit graph.
                var factor = unitFactor(productId, edge.TargetUnitId, unitId.Value);
                if (factor.IsSuccess && factor.Value > 0m)
                {
                    var landingFactor = (edge.TargetQuantity / edge.SubstituteQuantity) * factor.Value;
                    var landedLots = substituteLots
                        .Select(l => new ConvertedStockLot(l.QuantityInTargetUnit * landingFactor, l.ExpiryDate))
                        .ToList();
                    var landedContribution = landedLots.Sum(l => l.QuantityInTargetUnit);
                    substituteContribution += landedContribution;

                    // Preserve the display contract: every edge that landed a positive contribution is
                    // named, even when an earlier edge already supplied the remaining cookability gap.
                    // Waste evidence is narrower and remains allocation-limited below, so surplus stock
                    // from this edge cannot earn a Waste benefit.
                    if (landedContribution > 0m)
                        contributingSubstitutes.Add(edge.SubstituteProductId);

                    if (remainingSubstituteRequirement > 0m)
                    {
                        var allocation = AllocateLots(
                            landedLots, remainingSubstituteRequirement, today, expiringSoonDays);
                        remainingSubstituteRequirement -= allocation.AllocatedQuantity;
                        hasContributingExpiringStock |= allocation.HasContributingExpiringStock;
                    }
                }
                // Conversion failure on the landing hop contributes zero — same partial-visibility rule.
            }
        }

        var combinedAvailable = directAvailable + substituteContribution;

        var status = combinedAvailable <= 0m ? IngredientStatus.Missing
            : combinedAvailable < scaledRequired ? IngredientStatus.Low
            : directAvailable >= scaledRequired ? IngredientStatus.InStock
            : IngredientStatus.InStockViaSubstitute;

        // Display-only: a Missing that is really "we hold stock we can't convert to compare", not an
        // empty pantry (plantry-z2sr). Status stays Missing so cookability / shortfall / shopping are
        // unchanged — only the UI reads this flag to swap the "Not in your pantry" copy for an honest
        // "can't compare units" explanation. Mirrors the Cook page's IsUnitGap treatment (plantry-qll2.5).
        // Scoped to the line's own direct stock (not substitutes) — this flag is about the line's own
        // pantry being uncomparable, not about a substitute's.
        var unitMismatch = combinedAvailable <= 0m && hadUnconvertibleStock;

        // Expiry-soon flag (J1/J3): signed days when soonest expiry is within the household's horizon.
        int? expiresWithinDays = null;
        if (soonestExpiry is { } soonest)
        {
            var daysUntilExpiry = soonest.DayNumber - today.DayNumber;
            if (daysUntilExpiry <= expiringSoonDays)
                expiresWithinDays = daysUntilExpiry;
        }

        return (
            status,
            expiresWithinDays,
            combinedAvailable > 0m ? combinedAvailable : null,
            unitMismatch,
            contributingSubstitutes,
            hasContributingExpiringStock);
    }

    private sealed record ConvertedStockLot(decimal QuantityInTargetUnit, DateOnly? ExpiryDate);

    private static (decimal AllocatedQuantity, bool HasContributingExpiringStock) AllocateLots(
        IReadOnlyList<ConvertedStockLot> lots,
        decimal requiredQuantity,
        DateOnly today,
        int expiringSoonDays)
    {
        var remaining = Math.Max(0m, requiredQuantity);
        var allocated = 0m;
        var hasContributingExpiringStock = false;

        // A parent product and a substitute parent can contribute lots from several concrete
        // products. Apply FEFO after conversion/landing, not just per product, so an earlier
        // non-expiring variant cannot consume the allocation before a later expiring variant.
        foreach (var lot in lots
            .OrderBy(l => l.ExpiryDate is null)
            .ThenBy(l => l.ExpiryDate ?? DateOnly.MaxValue))
        {
            if (remaining <= 0m) break;

            var amount = Math.Min(remaining, lot.QuantityInTargetUnit);
            if (amount <= 0m) continue;

            allocated += amount;
            remaining -= amount;

            if (lot.ExpiryDate is { } expiry)
            {
                var daysUntilExpiry = expiry.DayNumber - today.DayNumber;
                if (daysUntilExpiry >= 0 && daysUntilExpiry <= expiringSoonDays)
                    hasContributingExpiringStock = true;
            }
        }

        return (allocated, hasContributingExpiringStock);
    }

    private static FulfillmentOverall BuildOverall(IEnumerable<IngredientStatus> statuses)
    {
        var missing = 0;
        var low = 0;
        foreach (var s in statuses)
        {
            if (s == IngredientStatus.Missing) missing++;
            else if (s == IngredientStatus.Low) low++;
        }

        if (missing == 0 && low == 0)
            return new FulfillmentOverall(FullyCookable: true, MissingCount: 0, LowCount: 0);

        return new FulfillmentOverall(FullyCookable: false, MissingCount: missing, LowCount: low);
    }
}

// ── Value objects ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Availability status for one ingredient in the context of a specific desired serving count
/// (recipes-domain-model.md §6).
/// </summary>
public enum IngredientStatus
{
    /// <summary>
    /// Tracked product with sufficient stock: available &gt;= scaled required (in ingredient unit).
    /// </summary>
    InStock,

    /// <summary>
    /// Tracked product with partial stock: 0 &lt; available &lt; scaled required.
    /// </summary>
    Low,

    /// <summary>
    /// Tracked product with zero available stock (no active lots, or all lots depleted).
    /// </summary>
    Missing,

    /// <summary>
    /// Untracked staple (<c>track_stock = false</c>) — always treated as satisfied (C12).
    /// </summary>
    Untracked,

    /// <summary>
    /// Tracked product whose direct (incl. DM-19 variant rollup) stock alone was insufficient, but
    /// one-hop substitution edges (plantry-aqpa.1/aqpa.2) bring the combined direct+substitute closure
    /// up to (or past) the scaled required quantity. Distinct from <see cref="InStock"/> — the cook
    /// deciding tonight's dinner cares which.
    /// </summary>
    InStockViaSubstitute,
}

/// <summary>
/// Fulfillment result for a single ingredient line.
/// </summary>
/// <param name="IngredientId">The local ingredient this result covers.</param>
/// <param name="Status">Availability classification.</param>
/// <param name="ExpiresWithinDays">
/// Signed integer set when the soonest active lot's expiry is within the household's configured
/// "expiring soon" horizon of today (including past dates); null when no expiry applies or expiry is beyond it.
/// Negative = days past use-by (expired); 0 = expires today; positive = days until expiry.
/// </param>
/// <param name="AvailableQuantity">
/// Available quantity in the ingredient's unit; null when nothing is available or the ingredient is
/// untracked.
/// </param>
/// <param name="UnitMismatch">
/// Display-only (plantry-z2sr): true when <paramref name="Status"/> is Missing <b>only</b> because
/// real on-hand stock could not be converted to the recipe unit — the pantry can't be compared, it is
/// not empty. Lets the UI show an honest "can't compare units" explanation instead of "Not in your
/// pantry". Never affects the status or the cookability rollup.
/// </param>
/// <param name="ContributingSubstituteProductIds">
/// Display-only (plantry-aqpa.5): substitute products (soft ref → catalog.product) whose stock landed a
/// positive contribution toward this line, in no particular order. Populated whenever a substitution
/// edge contributed — <b>not only</b> when <paramref name="Status"/> is
/// <see cref="IngredientStatus.InStockViaSubstitute"/>: a <see cref="IngredientStatus.Low"/> line that
/// substitutes only partially covered also carries entries here. The UI (Recipe Details' ingredient
/// row) reads this list only in the <c>InStockViaSubstitute</c> branch; several edges may all
/// contribute, and the UI names them without claiming a precise per-substitute quantity split.
/// </param>
/// <param name="HasContributingExpiringStock">
/// True only when a positive quantity allocated to satisfy this line came from an active lot whose expiry
/// is today or later and falls within the configured expiring-soon horizon. Expired stock, or stock that
/// merely exists but is not needed by the line, never sets this flag.
/// </param>
public sealed record IngredientFulfillment(
    IngredientId IngredientId,
    IngredientStatus Status,
    int? ExpiresWithinDays,
    decimal? AvailableQuantity,
    bool UnitMismatch = false,
    IReadOnlyList<Guid>? ContributingSubstituteProductIds = null,
    bool HasContributingExpiringStock = false);

/// <summary>
/// Top-level summary of whether a recipe is fully cookable.
/// </summary>
/// <param name="FullyCookable">True when all tracked ingredients are InStock at the given serving count.</param>
/// <param name="MissingCount">Number of ingredients with <see cref="IngredientStatus.Missing"/>.</param>
/// <param name="LowCount">Number of ingredients with <see cref="IngredientStatus.Low"/>.</param>
public sealed record FulfillmentOverall(bool FullyCookable, int MissingCount, int LowCount);

/// <summary>
/// The complete cookability computation for one recipe at a given serving count
/// (recipes-domain-model.md §6). Never persisted — computed fresh from live Inventory reads.
/// </summary>
/// <param name="Overall">Top-level cookability summary.</param>
/// <param name="Lines">Per-ingredient fulfillment details, in ingredient ordinal order.</param>
public sealed record FulfillmentResult(
    FulfillmentOverall Overall,
    IReadOnlyList<IngredientFulfillment> Lines);

/// <summary>
/// Fulfillment result for one <b>expanded</b> product-level line (recipe-composition.md §7). Keyed by
/// <c>(ProductId, UnitId)</c> — the aggregation grain of the expanded view (D14) — rather than an
/// <see cref="IngredientId"/>, because an expanded product may originate from several ingredients across a
/// recipe's inclusion tree.
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3); null for an untracked staple.</param>
/// <param name="Status">Availability classification for this product at the requested servings.</param>
/// <param name="ExpiresWithinDays">Signed expiry-soon days (see <see cref="IngredientFulfillment.ExpiresWithinDays"/>).</param>
/// <param name="AvailableQuantity">Available quantity in the line's unit; null when nothing is available or untracked.</param>
/// <param name="UnitMismatch">
/// Display-only (plantry-z2sr): true when <paramref name="Status"/> is Missing <b>only</b> because real
/// on-hand stock could not be converted to the recipe unit (see <see cref="IngredientFulfillment.UnitMismatch"/>).
/// </param>
/// <param name="ContributingSubstituteProductIds">
/// Display-only (plantry-aqpa.5): see <see cref="IngredientFulfillment.ContributingSubstituteProductIds"/>.
/// </param>
/// <param name="HasContributingExpiringStock">
/// True only when a positive quantity allocated to satisfy this expanded line came from a non-expired
/// active lot within the configured expiring-soon horizon.
/// </param>
public sealed record ExpandedIngredientFulfillment(
    Guid ProductId,
    Guid? UnitId,
    IngredientStatus Status,
    int? ExpiresWithinDays,
    decimal? AvailableQuantity,
    bool UnitMismatch = false,
    IReadOnlyList<Guid>? ContributingSubstituteProductIds = null,
    bool HasContributingExpiringStock = false);

/// <summary>
/// The complete cookability computation over a recipe's <b>expanded</b> view at a given serving count
/// (recipe-composition.md §7). Never persisted — computed fresh from live Inventory reads.
/// </summary>
/// <param name="Overall">Top-level cookability summary over the expanded product set.</param>
/// <param name="Lines">Per-expanded-product fulfillment, one row per aggregated <c>(ProductId, UnitId)</c>.</param>
public sealed record ExpandedFulfillmentResult(
    FulfillmentOverall Overall,
    IReadOnlyList<ExpandedIngredientFulfillment> Lines);
