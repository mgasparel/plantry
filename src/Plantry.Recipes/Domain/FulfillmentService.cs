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

        // Compute per-ingredient fulfillment, keyed by the ingredient's own id (flat view).
        var lines = new List<IngredientFulfillment>(recipe.Ingredients.Count);
        foreach (var ingredient in recipe.Ingredients)
        {
            var (status, expires, available) = await ComputeLineAsync(
                ingredient.ProductId, ingredient.Quantity, ingredient.UnitId,
                scale, catalogById, stockById, today, expiringSoonDays, ct);
            lines.Add(new IngredientFulfillment(ingredient.Id, status, expires, available));
        }

        var overall = BuildOverall(lines.Select(l => l.Status));
        return new FulfillmentResult(overall, lines);
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

        var resultLines = new List<ExpandedIngredientFulfillment>(lines.Count);
        foreach (var line in lines)
        {
            var (status, expires, available) = await ComputeLineAsync(
                line.ProductId, line.Quantity, line.UnitId,
                scale, catalogById, stockById, today, expiringSoonDays, ct);
            resultLines.Add(new ExpandedIngredientFulfillment(
                line.ProductId, line.UnitId, status, expires, available));
        }

        var overall = BuildOverall(resultLines.Select(l => l.Status));
        return new ExpandedFulfillmentResult(overall, resultLines);
    }

    /// <summary>
    /// Batch-resolves catalog facts (track_stock + parent/variant tree, DM-19) for the given product ids and
    /// reads a single batch stock snapshot for every tracked leaf / variant child. Sequential catalog awaits
    /// are required: the adapter runs over the scoped Catalog EF DbContext which forbids concurrent operations
    /// on the same instance (Task.WhenAll would throw InvalidOperationException for ≥2 distinct product ids).
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, CatalogProduct> Catalog, IReadOnlyDictionary<Guid, Application.ProductStock> Stock)>
        ResolveCatalogAndStockAsync(IReadOnlyList<Guid> productIds, CancellationToken ct)
    {
        var catalogById = new Dictionary<Guid, CatalogProduct>(productIds.Count);
        foreach (var productId in productIds)
        {
            var product = await catalogReader.FindAsync(productId, ct);
            if (product is not null)
                catalogById[product.Id] = product;
        }

        // Collect all product ids we actually need stock for: tracked leaf products, and the
        // variant children of any parent-product ingredients (DM-19 rollup).
        var stockProductIds = new HashSet<Guid>();
        foreach (var productId in productIds)
        {
            if (!catalogById.TryGetValue(productId, out var catalogProduct))
                continue;
            if (!catalogProduct.TrackStock)
                continue; // untracked — no stock query needed

            if (catalogProduct.IsParent)
            {
                foreach (var variantId in catalogProduct.VariantProductIds)
                    stockProductIds.Add(variantId);
            }
            else
            {
                stockProductIds.Add(productId);
            }
        }

        var stockById = stockProductIds.Count > 0
            ? await stockReader.FindStockBatchAsync(stockProductIds.ToList(), ct)
            : new Dictionary<Guid, Application.ProductStock>();

        return (catalogById, stockById);
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

        var lines = new List<IngredientFulfillment>(recipe.Ingredients.Count);
        foreach (var ingredient in recipe.Ingredients)
        {
            var fulfillment = ComputeIngredientPure(ingredient, scale, catalogById, stockById, today, converter, expiringSoonDays);
            lines.Add(fulfillment);
        }

        var overall = BuildOverall(lines.Select(l => l.Status));
        return new FulfillmentResult(overall, lines);
    }

    private static IngredientFulfillment ComputeIngredientPure(
        Ingredient ingredient,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        DateOnly today,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter,
        int expiringSoonDays)
    {
        // If we can't resolve the product from catalog, treat as Missing.
        if (!catalogById.TryGetValue(ingredient.ProductId, out var catalogProduct))
        {
            return new IngredientFulfillment(
                ingredient.Id, IngredientStatus.Missing, null, null);
        }

        // Untracked staples are always satisfied (C12).
        if (!catalogProduct.TrackStock)
        {
            return new IngredientFulfillment(
                ingredient.Id, IngredientStatus.Untracked, null, null);
        }

        // Null quantity/unit means untracked staple ("to taste") — defensive (R5).
        if (ingredient.Quantity is null || ingredient.UnitId is null)
        {
            return new IngredientFulfillment(
                ingredient.Id, IngredientStatus.Untracked, null, null);
        }

        var scaledRequired = ingredient.Quantity.Value * scale;

        decimal totalAvailableInIngredientUnit = 0m;
        DateOnly? soonestExpiry = null;

        if (catalogProduct.IsParent)
        {
            // Sum stock across all non-archived variant children (DM-19 rollup).
            foreach (var variantId in catalogProduct.VariantProductIds)
            {
                if (!stockById.TryGetValue(variantId, out var variantStock))
                    continue;

                var converted = converter(
                    variantId,
                    variantStock.AvailableQuantity,
                    variantStock.DefaultUnitId,
                    ingredient.UnitId.Value);

                if (converted.IsSuccess)
                    totalAvailableInIngredientUnit += converted.Value;

                if (variantStock.SoonestExpiry.HasValue)
                {
                    if (soonestExpiry is null || variantStock.SoonestExpiry.Value < soonestExpiry.Value)
                        soonestExpiry = variantStock.SoonestExpiry;
                }
            }
        }
        else
        {
            // Leaf product: single stock record.
            if (stockById.TryGetValue(ingredient.ProductId, out var stock))
            {
                var converted = converter(
                    ingredient.ProductId,
                    stock.AvailableQuantity,
                    stock.DefaultUnitId,
                    ingredient.UnitId.Value);

                if (converted.IsSuccess)
                    totalAvailableInIngredientUnit = converted.Value;

                soonestExpiry = stock.SoonestExpiry;
            }
        }

        IngredientStatus status;
        if (totalAvailableInIngredientUnit <= 0m)
            status = IngredientStatus.Missing;
        else if (totalAvailableInIngredientUnit < scaledRequired)
            status = IngredientStatus.Low;
        else
            status = IngredientStatus.InStock;

        int? expiresWithinDays = null;
        if (soonestExpiry.HasValue)
        {
            var daysUntilExpiry = soonestExpiry.Value.DayNumber - today.DayNumber;
            if (daysUntilExpiry <= expiringSoonDays)
                expiresWithinDays = daysUntilExpiry;
        }

        return new IngredientFulfillment(
            ingredient.Id,
            status,
            expiresWithinDays,
            totalAvailableInIngredientUnit > 0m ? totalAvailableInIngredientUnit : null);
    }

    /// <summary>
    /// Core per-line availability computation shared by the flat (<see cref="ComputeAsync(Recipe,int,DateOnly,CancellationToken)"/>)
    /// and expanded (<see cref="ComputeExpandedAsync"/>) paths — keyed only on the product/quantity/unit so
    /// it is agnostic to whether the line came from a direct ingredient or an aggregated expanded line. Returns
    /// the availability status, the signed expiry-soon days (or null), and the available quantity in the line's
    /// unit (or null when nothing is available / the line is untracked).
    /// </summary>
    private async Task<(IngredientStatus Status, int? ExpiresWithinDays, decimal? AvailableQuantity)> ComputeLineAsync(
        Guid productId,
        decimal? quantity,
        Guid? unitId,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, Application.ProductStock> stockById,
        DateOnly today,
        int expiringSoonDays,
        CancellationToken ct)
    {
        // If we can't resolve the product from catalog, treat as Missing.
        if (!catalogById.TryGetValue(productId, out var catalogProduct))
            return (IngredientStatus.Missing, null, null);

        // Untracked staples are always satisfied (C12).
        if (!catalogProduct.TrackStock)
            return (IngredientStatus.Untracked, null, null);

        // Null quantity/unit means untracked staple ("to taste") — should match track_stock = false,
        // but be defensive and treat as Untracked here too (R5).
        if (quantity is null || unitId is null)
            return (IngredientStatus.Untracked, null, null);

        // Scale the required quantity to the desired servings.
        var scaledRequired = quantity.Value * scale;

        // Collect stock snapshot(s) for this line.
        // Parent product (DM-19): roll up stock across all variant children.
        decimal totalAvailableInLineUnit = 0m;
        DateOnly? soonestExpiry = null;

        if (catalogProduct.IsParent)
        {
            // Sum stock across all non-archived variant children.
            foreach (var variantId in catalogProduct.VariantProductIds)
            {
                if (!stockById.TryGetValue(variantId, out var variantStock))
                    continue; // variant has no stock record → contributes 0

                // Convert variant's available quantity from its default unit to the line's unit.
                var converted = await unitConverter.ConvertAsync(
                    variantId,
                    variantStock.AvailableQuantity,
                    variantStock.DefaultUnitId,
                    unitId.Value,
                    ct);

                if (converted.IsSuccess)
                    totalAvailableInLineUnit += converted.Value;
                // On conversion failure, the variant contributes 0 — partial visibility is better than crash.

                // Track soonest expiry across variants.
                if (variantStock.SoonestExpiry.HasValue)
                {
                    if (soonestExpiry is null || variantStock.SoonestExpiry.Value < soonestExpiry.Value)
                        soonestExpiry = variantStock.SoonestExpiry;
                }
            }
        }
        else
        {
            // Leaf product: single stock record.
            if (stockById.TryGetValue(productId, out var stock))
            {
                var converted = await unitConverter.ConvertAsync(
                    productId,
                    stock.AvailableQuantity,
                    stock.DefaultUnitId,
                    unitId.Value,
                    ct);

                if (converted.IsSuccess)
                    totalAvailableInLineUnit = converted.Value;

                soonestExpiry = stock.SoonestExpiry;
            }
            // else: no stock record → totalAvailable stays 0
        }

        // Classify status.
        IngredientStatus status;
        if (totalAvailableInLineUnit <= 0m)
            status = IngredientStatus.Missing;
        else if (totalAvailableInLineUnit < scaledRequired)
            status = IngredientStatus.Low;
        else
            status = IngredientStatus.InStock;

        // Expiry-soon flag (J1/J3): soonest expiry within the household's configured horizon of today.
        int? expiresWithinDays = null;
        if (soonestExpiry.HasValue)
        {
            var daysUntilExpiry = soonestExpiry.Value.DayNumber - today.DayNumber;
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
