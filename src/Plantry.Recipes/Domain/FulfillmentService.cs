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
///   <item>InStock when available &gt;= required; Low when 0 &lt; available &lt; required; Missing when available == 0.</item>
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
    IExpiringSoonHorizonReader horizonReader)
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

        var (catalogById, stockById) = await ResolveCatalogAndStockAsync(allProductIds, ct);

        // Pre-resolve every unit conversion the pure rule core will need, so the flat computation runs
        // entirely in-memory (ADR-021: async fetches the data up front, the pure core does the math).
        var converter = await ResolveConverterAsync(
            recipe.Ingredients.Select(i => (i.ProductId, i.UnitId)), catalogById, stockById, ct);

        // Delegate to the same pure rule core the sync Compute overload uses (single rule engine).
        return ComputeFlat(recipe.Ingredients, scale, today, catalogById, stockById, converter, expiringSoonDays);
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
        var (catalogById, stockById) = await ResolveCatalogAndStockAsync(allProductIds, ct);

        // Pre-resolve conversions, then run the shared pure rule core per line — identical logic to the
        // flat path, keyed on product/quantity/unit so a flat recipe yields identical statuses.
        var converter = await ResolveConverterAsync(
            lines.Select(l => (l.ProductId, l.UnitId)), catalogById, stockById, ct);

        var resultLines = new List<ExpandedIngredientFulfillment>(lines.Count);
        foreach (var line in lines)
        {
            var (status, expires, available) = ComputeLineCore(
                line.ProductId, line.Quantity, line.UnitId,
                scale, catalogById, stockById, today, converter, expiringSoonDays);
            resultLines.Add(new ExpandedIngredientFulfillment(
                line.ProductId, line.UnitId, status, expires, available));
        }

        var overall = BuildOverall(resultLines.Select(l => l.Status));
        return new ExpandedFulfillmentResult(overall, resultLines);
    }

    /// <summary>
    /// Batch-resolves catalog facts (track_stock + parent/variant tree, DM-19) and stock snapshots for the
    /// given product ids — each in a single round-trip (one catalog batch, one stock batch), replacing the
    /// former per-product catalog N+1.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, CatalogProduct> Catalog, IReadOnlyDictionary<Guid, ProductStock> Stock)>
        ResolveCatalogAndStockAsync(IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        // One batch round-trip for the catalog facts + variant tree (was one FindAsync per product).
        var catalogById = await catalogReader.FindManyWithVariantsAsync(productIds, ct);

        // Collect all product ids we actually need stock for: tracked leaf products, and the
        // variant children of any parent-product ingredients (DM-19 rollup).
        var stockProductIds = new HashSet<Guid>();
        foreach (var productId in productIds)
        {
            if (!catalogById.TryGetValue(productId, out var catalogProduct) || !catalogProduct.TrackStock)
                continue; // absent or untracked — no stock query needed

            foreach (var stockRef in StockRefsFor(catalogProduct, productId))
                stockProductIds.Add(stockRef);
        }

        var stockById = stockProductIds.Count > 0
            ? await stockReader.FindStockBatchAsync(stockProductIds.ToList(), ct)
            : new Dictionary<Guid, ProductStock>();

        return (catalogById, stockById);
    }

    /// <summary>
    /// Pre-resolves every unit conversion the pure rule core will need for the given lines into an
    /// in-memory lookup, so the core can run fully synchronously (ADR-021 rule 1: SQL/async fetches the
    /// data, C# keeps the math). Conversions are awaited <b>sequentially</b> — the converter adapter runs
    /// over the scoped Catalog EF DbContext, which forbids concurrent operations on one instance. Each
    /// distinct (stock ref, from-unit, to-unit) is resolved once (repeats across lines are deduplicated).
    /// The returned delegate is the sync converter the core consumes; a stock ref whose conversion had no
    /// path resolves to a loud failure, so the core treats it as a zero contribution (partial visibility).
    /// </summary>
    private async Task<Func<Guid, decimal, Guid, Guid, Result<decimal>>> ResolveConverterAsync(
        IEnumerable<(Guid ProductId, Guid? UnitId)> lines,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        CancellationToken ct)
    {
        var resolved = new Dictionary<(Guid StockRef, Guid FromUnit, Guid ToUnit), Result<decimal>>();
        foreach (var (productId, unitId) in lines)
        {
            if (unitId is null) continue; // untracked line — no conversion needed
            if (!catalogById.TryGetValue(productId, out var catalogProduct) || !catalogProduct.TrackStock)
                continue;

            foreach (var stockRef in StockRefsFor(catalogProduct, productId))
            {
                if (!stockById.TryGetValue(stockRef, out var stock)) continue;
                var key = (stockRef, stock.DefaultUnitId, unitId.Value);
                if (resolved.ContainsKey(key)) continue;
                resolved[key] = await unitConverter.ConvertAsync(
                    stockRef, stock.AvailableQuantity, stock.DefaultUnitId, unitId.Value, ct);
            }
        }

        return (stockRef, _, fromUnit, toUnit) =>
            resolved.GetValueOrDefault((stockRef, fromUnit, toUnit), ConversionUnavailable);
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
    /// parent product.</param>
    /// <param name="stockById">Pre-loaded stock snapshots keyed by product id — includes variant
    /// children; products with no active stock are absent (treated as zero).</param>
    /// <param name="converter">Sync unit conversion delegate: (productId, amount, fromUnitId, toUnitId) → Result.</param>
    /// <param name="expiringSoonDays">The household's "expiring soon" horizon in days — the caller reads it
    /// once via <see cref="IExpiringSoonHorizonReader"/> and passes it in (ADR-021: the pure overload does no IO).</param>
    public FulfillmentResult Compute(
        Recipe recipe,
        int desiredServings,
        DateOnly today,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        int expiringSoonDays)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;
        // Same pure rule core as the async ComputeAsync path — the caller having pre-loaded the data is
        // the only difference (MealPlanning borrows pre-computed enrichment facts, ADR-021).
        return ComputeFlat(recipe.Ingredients, scale, today, catalogById, stockById, converter, expiringSoonDays);
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
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        int expiringSoonDays)
    {
        var lines = new List<IngredientFulfillment>(ingredients.Count);
        foreach (var ingredient in ingredients)
        {
            var (status, expires, available) = ComputeLineCore(
                ingredient.ProductId, ingredient.Quantity, ingredient.UnitId,
                scale, catalogById, stockById, today, converter, expiringSoonDays);
            lines.Add(new IngredientFulfillment(ingredient.Id, status, expires, available));
        }

        return new FulfillmentResult(BuildOverall(lines.Select(l => l.Status)), lines);
    }

    /// <summary>
    /// The single pure cookability rule engine — the one place the status rules live (C12 untracked, R5
    /// defensive null qty/unit, DM-19 parent/variant stock rollup, unit-conversion comparison, signed
    /// J1/J3 expiry-soon horizon). Keyed only on product/quantity/unit so it is agnostic to whether the
    /// line came from a direct ingredient (flat) or an aggregated expanded line, and to whether the
    /// converter is live (async path, pre-resolved) or caller-supplied (pure overload). Returns the
    /// availability status, the signed expiry-soon days (or null), and the available quantity in the
    /// line's unit (or null when nothing is available / the line is untracked). Does no IO.
    /// </summary>
    private static (IngredientStatus Status, int? ExpiresWithinDays, decimal? AvailableQuantity) ComputeLineCore(
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        DateOnly today,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        int expiringSoonDays)
    {
        // Unresolvable product → Missing.
        if (!catalogById.TryGetValue(productId, out var catalogProduct))
            return (IngredientStatus.Missing, null, null);

        // Untracked staple (track_stock = false) is always satisfied (C12) — and, defensively, a null
        // quantity/unit ("to taste") is treated the same even on a tracked product (R5).
        if (!catalogProduct.TrackStock || quantity is null || unitId is null)
            return (IngredientStatus.Untracked, null, null);

        var scaledRequired = quantity.Value * scale;

        // Roll up available stock (in the line's unit) and soonest expiry across the line's stock refs:
        // a leaf draws from itself; a parent (DM-19) sums across its live variant children.
        decimal totalAvailableInLineUnit = 0m;
        DateOnly? soonestExpiry = null;
        foreach (var stockRef in StockRefsFor(catalogProduct, productId))
        {
            if (!stockById.TryGetValue(stockRef, out var stock))
                continue; // no stock record → contributes 0

            var converted = converter(stockRef, stock.AvailableQuantity, stock.DefaultUnitId, unitId.Value);
            if (converted.IsSuccess)
                totalAvailableInLineUnit += converted.Value;
            // On conversion failure the ref contributes 0 — partial visibility is better than a crash.

            if (stock.SoonestExpiry is { } expiry &&
                (soonestExpiry is null || expiry < soonestExpiry.Value))
                soonestExpiry = expiry;
        }

        var status = totalAvailableInLineUnit <= 0m ? IngredientStatus.Missing
            : totalAvailableInLineUnit < scaledRequired ? IngredientStatus.Low
            : IngredientStatus.InStock;

        // Expiry-soon flag (J1/J3): signed days when soonest expiry is within the household's horizon.
        int? expiresWithinDays = null;
        if (soonestExpiry is { } soonest)
        {
            var daysUntilExpiry = soonest.DayNumber - today.DayNumber;
            if (daysUntilExpiry <= expiringSoonDays)
                expiresWithinDays = daysUntilExpiry;
        }

        return (status, expiresWithinDays, totalAvailableInLineUnit > 0m ? totalAvailableInLineUnit : null);
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
public sealed record IngredientFulfillment(
    IngredientId IngredientId,
    IngredientStatus Status,
    int? ExpiresWithinDays,
    decimal? AvailableQuantity);

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
public sealed record ExpandedIngredientFulfillment(
    Guid ProductId,
    Guid? UnitId,
    IngredientStatus Status,
    int? ExpiresWithinDays,
    decimal? AvailableQuantity);

/// <summary>
/// The complete cookability computation over a recipe's <b>expanded</b> view at a given serving count
/// (recipe-composition.md §7). Never persisted — computed fresh from live Inventory reads.
/// </summary>
/// <param name="Overall">Top-level cookability summary over the expanded product set.</param>
/// <param name="Lines">Per-expanded-product fulfillment, one row per aggregated <c>(ProductId, UnitId)</c>.</param>
public sealed record ExpandedFulfillmentResult(
    FulfillmentOverall Overall,
    IReadOnlyList<ExpandedIngredientFulfillment> Lines);
