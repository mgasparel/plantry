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
///   <item>ExpiresWithinDays is a <b>signed</b> integer set when soonest expiry ≤ 4 days from today (J1/J3): negative = days past use-by (expired); 0 = expires today; positive = days until expiry.</item>
/// </list>
/// </summary>
public sealed class FulfillmentService(
    IInventoryStockReader stockReader,
    ICatalogProductReader catalogReader,
    IUnitConverter unitConverter)
{
    /// <summary>
    /// Days threshold for the "Use soon" expiry warning (J1/J3).
    /// </summary>
    public const int ExpiringSoonDays = 4;

    /// <summary>
    /// Computes the <see cref="FulfillmentResult"/> for <paramref name="recipe"/> at
    /// <paramref name="desiredServings"/>. All stock reads are performed in one batch round-trip.
    /// </summary>
    public async Task<FulfillmentResult> ComputeAsync(
        Recipe recipe,
        int desiredServings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        // Collect all distinct product ids from the ingredient list so we can batch-resolve
        // catalog facts (track_stock, parent/variant tree) and stock snapshots.
        var allProductIds = recipe.Ingredients
            .Select(i => i.ProductId)
            .Distinct()
            .ToList();

        // Resolve catalog product facts (track_stock + parent/variant tree, DM-19).
        // Sequential awaits are required: the adapter runs over the scoped Catalog EF DbContext
        // which forbids concurrent operations on the same instance. Task.WhenAll would throw
        // InvalidOperationException for recipes with ≥2 distinct product ids.
        var catalogById = new Dictionary<Guid, CatalogProduct>(allProductIds.Count);
        foreach (var productId in allProductIds)
        {
            var product = await catalogReader.FindAsync(productId, ct);
            if (product is not null)
                catalogById[product.Id] = product;
        }

        // Collect all product ids we actually need stock for: tracked leaf products, and the
        // variant children of any parent-product ingredients (DM-19 rollup).
        var stockProductIds = new HashSet<Guid>();
        foreach (var productId in allProductIds)
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

        // Single batch stock read for all relevant product ids.
        var stockById = stockProductIds.Count > 0
            ? await stockReader.FindStockBatchAsync(stockProductIds.ToList(), ct)
            : new Dictionary<Guid, Application.ProductStock>();

        // Compute per-ingredient fulfillment.
        var lines = new List<IngredientFulfillment>(recipe.Ingredients.Count);
        foreach (var ingredient in recipe.Ingredients)
        {
            var fulfillment = await ComputeIngredientAsync(
                ingredient, scale, catalogById, stockById, today, ct);
            lines.Add(fulfillment);
        }

        var overall = BuildOverall(lines);
        return new FulfillmentResult(overall, lines);
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
    public FulfillmentResult Compute(
        Recipe recipe,
        int desiredServings,
        DateOnly today,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        var lines = new List<IngredientFulfillment>(recipe.Ingredients.Count);
        foreach (var ingredient in recipe.Ingredients)
        {
            var fulfillment = ComputeIngredientPure(ingredient, scale, catalogById, stockById, today, converter);
            lines.Add(fulfillment);
        }

        var overall = BuildOverall(lines);
        return new FulfillmentResult(overall, lines);
    }

    private static IngredientFulfillment ComputeIngredientPure(
        Ingredient ingredient,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, ProductStock> stockById,
        DateOnly today,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
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
            if (daysUntilExpiry <= ExpiringSoonDays)
                expiresWithinDays = daysUntilExpiry;
        }

        return new IngredientFulfillment(
            ingredient.Id,
            status,
            expiresWithinDays,
            totalAvailableInIngredientUnit > 0m ? totalAvailableInIngredientUnit : null);
    }

    private async Task<IngredientFulfillment> ComputeIngredientAsync(
        Ingredient ingredient,
        decimal scale,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        IReadOnlyDictionary<Guid, Application.ProductStock> stockById,
        DateOnly today,
        CancellationToken ct)
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

        // Null quantity/unit means untracked staple ("to taste") — should match track_stock = false,
        // but be defensive and treat as Untracked here too (R5).
        if (ingredient.Quantity is null || ingredient.UnitId is null)
        {
            return new IngredientFulfillment(
                ingredient.Id, IngredientStatus.Untracked, null, null);
        }

        // Scale the required quantity to the desired servings.
        var scaledRequired = ingredient.Quantity.Value * scale;

        // Collect stock snapshot(s) for this ingredient.
        // Parent product (DM-19): roll up stock across all variant children.
        decimal totalAvailableInIngredientUnit = 0m;
        DateOnly? soonestExpiry = null;

        if (catalogProduct.IsParent)
        {
            // Sum stock across all non-archived variant children.
            foreach (var variantId in catalogProduct.VariantProductIds)
            {
                if (!stockById.TryGetValue(variantId, out var variantStock))
                    continue; // variant has no stock record → contributes 0

                // Convert variant's available quantity from its default unit to the ingredient's unit.
                var converted = await unitConverter.ConvertAsync(
                    variantId,
                    variantStock.AvailableQuantity,
                    variantStock.DefaultUnitId,
                    ingredient.UnitId.Value,
                    ct);

                if (converted.IsSuccess)
                    totalAvailableInIngredientUnit += converted.Value;
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
            if (stockById.TryGetValue(ingredient.ProductId, out var stock))
            {
                var converted = await unitConverter.ConvertAsync(
                    ingredient.ProductId,
                    stock.AvailableQuantity,
                    stock.DefaultUnitId,
                    ingredient.UnitId.Value,
                    ct);

                if (converted.IsSuccess)
                    totalAvailableInIngredientUnit = converted.Value;

                soonestExpiry = stock.SoonestExpiry;
            }
            // else: no stock record → totalAvailable stays 0
        }

        // Classify status.
        IngredientStatus status;
        if (totalAvailableInIngredientUnit <= 0m)
            status = IngredientStatus.Missing;
        else if (totalAvailableInIngredientUnit < scaledRequired)
            status = IngredientStatus.Low;
        else
            status = IngredientStatus.InStock;

        // Expiry-soon flag (J1/J3): soonest expiry within 4 days from today.
        int? expiresWithinDays = null;
        if (soonestExpiry.HasValue)
        {
            var daysUntilExpiry = soonestExpiry.Value.DayNumber - today.DayNumber;
            if (daysUntilExpiry <= ExpiringSoonDays)
                expiresWithinDays = daysUntilExpiry;
        }

        return new IngredientFulfillment(
            ingredient.Id,
            status,
            expiresWithinDays,
            totalAvailableInIngredientUnit > 0m ? totalAvailableInIngredientUnit : null);
    }

    private static FulfillmentOverall BuildOverall(IReadOnlyList<IngredientFulfillment> lines)
    {
        var missing = lines.Count(l => l.Status == IngredientStatus.Missing);
        var low = lines.Count(l => l.Status == IngredientStatus.Low);

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
/// Signed integer set when the soonest active lot's expiry is within <see cref="FulfillmentService.ExpiringSoonDays"/>
/// days of today (including past dates); null when no expiry applies or expiry is beyond the threshold.
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
