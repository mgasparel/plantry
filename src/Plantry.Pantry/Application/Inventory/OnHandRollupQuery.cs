using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pantry.Application;

/// <summary>
/// One contribution to a parent's rolled-up on-hand total (plantry-oh27.2) — a single live variant's
/// own on-hand (in its own display unit) plus that amount converted into the parent's default unit.
/// <see cref="ConvertedOnHand"/> is null when the variant's unit could not be converted into the
/// parent's unit — the variant still appears here (never silently dropped) but is excluded from the
/// parent's <see cref="OnHandLevel.OnHand"/> sum and listed in <see cref="OnHandLevel.UnconvertedVariantIds"/>.
/// </summary>
public sealed record OnHandVariantContribution(
    Guid VariantId,
    string Name,
    decimal OnHand,
    Guid UnitId,
    string UnitCode,
    decimal? ConvertedOnHand);

/// <summary>
/// The on-hand rollup for one requested product id — a leaf's own on-hand, or a parent's Σ over its
/// live (non-archived) variants converted into the parent's default unit (plantry-oh27.2). See
/// <see cref="IOnHandRollupQuery"/> for the batching contract.
/// </summary>
public sealed record OnHandLevel(
    /// <summary>The requested id — a parent or a leaf.</summary>
    Guid ProductId,
    /// <summary>On-hand expressed in <see cref="UnitId"/>. For a parent this is the sum of every
    /// convertible live variant's on-hand; for a leaf it is exactly today's <c>InventoryQueryService.DisplayQuantity</c> result.</summary>
    decimal OnHand,
    Guid UnitId,
    string UnitCode,
    bool IsParent,
    /// <summary>Per-variant breakdown; always empty for a leaf.</summary>
    IReadOnlyList<OnHandVariantContribution> Variants,
    /// <summary>Live variants whose on-hand could not be expressed in the parent's unit — excluded from
    /// <see cref="OnHand"/>, never silently dropped. Always empty for a leaf.</summary>
    IReadOnlyList<Guid> UnconvertedVariantIds);

/// <summary>
/// One shared, parent-aware on-hand rollup for the Inventory application layer (plantry-oh27.2) — every
/// surface asking "how much of product X is on hand" gets the same answer whether X is a leaf or a
/// parent. Parents never own a <see cref="ProductStock"/> row (epic constraint, plantry-oh27); this is
/// a read model built purely by folding live variants' own stock at query time.
/// </summary>
public interface IOnHandRollupQuery
{
    /// <summary>
    /// Resolves on-hand for exactly the requested ids. An id absent from the catalog is simply absent
    /// from the result. Otherwise every requested id that exists gets an entry — including a leaf with
    /// no stock row, or a parent with zero live variants (or none with a stock row), which report
    /// <c>OnHand = 0</c> / <c>IsParent</c> accordingly rather than being omitted (rule 3: callers can
    /// render "out" rather than "unknown"). Contrast <see cref="ForHouseholdAsync"/>, which is a
    /// discovery scan and so only surfaces products that actually carry stock.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, OnHandLevel>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Every leaf with a stock row, plus every parent with at least one live variant that has a stock
    /// row — the full household on-hand picture in one batch.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, OnHandLevel>> ForHouseholdAsync(CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IOnHandRollupQuery"/> implementation. Batched loads only (rule 5, plantry-oh27.2):
/// one <see cref="IProductStockRepository.ListForHouseholdAsync"/>, one <see cref="ICatalogReadFacade.ListProductsAsync"/>
/// (which already carries every variant's <see cref="CatalogProductInfo.ParentProductId"/>, so parent→
/// variant grouping happens in memory with no per-parent round trip), and one
/// <see cref="IProductConversionProvider.ForProductsAsync"/> call for every leaf/variant converter needed.
/// A leaf's own figure reuses <see cref="InventoryQueryService.DisplayQuantity"/> exactly — this class
/// never forks that aggregation, only adds the parent fold on top of it.
/// </summary>
public sealed class OnHandRollupQuery(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    ITenantContext tenant) : IOnHandRollupQuery
{
    public async Task<IReadOnlyDictionary<Guid, OnHandLevel>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId || productIds.Count == 0)
            return new Dictionary<Guid, OnHandLevel>();

        var context = await LoadAsync(HouseholdId.From(householdId), ct);

        var result = new Dictionary<Guid, OnHandLevel>();
        foreach (var productId in productIds.Distinct())
        {
            if (!context.ProductsById.TryGetValue(productId, out var product))
                continue; // unknown product — absent from the result

            result[productId] = product.CanHoldStock
                ? context.StockByProduct.TryGetValue(productId, out var stock)
                    ? LeafLevel(product, stock, context.ConvertersByProduct, context.UnitCodes)
                    : ZeroLeafLevel(product) // rule 3 extends to a directly-requested never-stocked leaf too
                : ParentLevel(product, context.VariantsByParent.GetValueOrDefault(productId, []), context);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, OnHandLevel>> ForHouseholdAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return new Dictionary<Guid, OnHandLevel>();

        var context = await LoadAsync(HouseholdId.From(householdId), ct);

        var result = new Dictionary<Guid, OnHandLevel>();
        foreach (var (productId, stock) in context.StockByProduct)
        {
            if (!context.ProductsById.TryGetValue(productId, out var product) || !product.CanHoldStock)
                continue; // unknown product, or a stock row somehow left on a parent — never surfaced

            result[productId] = LeafLevel(product, stock, context.ConvertersByProduct, context.UnitCodes);
        }

        foreach (var (parentId, variants) in context.VariantsByParent)
        {
            if (!context.ProductsById.TryGetValue(parentId, out var parent))
                continue;
            if (!variants.Any(v => context.StockByProduct.ContainsKey(v.Id)))
                continue; // discovery rule: a parent is only surfaced once >= 1 live variant has a stock row

            result[parentId] = ParentLevel(parent, variants, context);
        }

        return result;
    }

    /// <summary>Everything both public methods need, loaded in exactly the three batched round trips
    /// rule 5 requires (one stock load, one catalog products load, one converter load) — never per id.</summary>
    private async Task<RollupContext> LoadAsync(HouseholdId householdId, CancellationToken ct)
    {
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        var stockByProduct = allStock.ToDictionary(s => s.ProductId);

        var products = await catalog.ListProductsAsync(ct);
        var productsById = products.ToDictionary(p => p.Id);
        // Live variants only — ListProductsAsync already excludes archived products, so every group
        // here is a live-variant group by construction (no separate IsArchived filter needed).
        var variantsByParent = products
            .Where(p => p.ParentProductId is not null)
            .GroupBy(p => p.ParentProductId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every id whose own on-hand/unit conversion might be needed: leaves with a stock row, plus
        // every live variant feeding a parent rollup (a variant can appear in both sets — Distinct).
        var neededIds = stockByProduct.Keys
            .Concat(variantsByParent.Values.SelectMany(vs => vs.Select(v => v.Id)))
            .Distinct()
            .ToList();
        var convertersByProduct = await conversions.ForProductsAsync(neededIds, ct);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);

        return new RollupContext(stockByProduct, productsById, variantsByParent, convertersByProduct, unitCodes);
    }

    /// <summary>Rule 3: a parent with no live variant contributing stock still resolves — 0, IsParent —
    /// rather than being treated as unknown. Shared by <see cref="ParentLevel"/>'s empty-variant-list case
    /// and by the same rule extended to a directly-requested never-stocked leaf in <see cref="ForProductsAsync"/>.</summary>
    private static OnHandLevel ZeroLeafLevel(CatalogProductInfo product) =>
        new(product.Id, 0m, product.DefaultUnitId, product.DefaultUnitCode, IsParent: false, Variants: [], UnconvertedVariantIds: []);

    private static OnHandLevel ParentLevel(
        CatalogProductInfo parent, IReadOnlyList<CatalogProductInfo> variants, RollupContext context)
    {
        var contributions = new List<OnHandVariantContribution>();
        var unconverted = new List<Guid>();
        var sum = 0m;

        foreach (var variant in variants)
        {
            var variantLevel = context.StockByProduct.TryGetValue(variant.Id, out var variantStock)
                ? LeafLevel(variant, variantStock, context.ConvertersByProduct, context.UnitCodes)
                // rule 4: a live variant with no ProductStock row contributes 0 and still appears.
                : ZeroLeafLevel(variant);

            // Rule 2: convert using the VARIANT's own converter (as EffectivePriceRollup does), never
            // the parent's — a variant's conversion overrides are its own.
            var variantConverter = context.ConvertersByProduct[variant.Id];
            var converted = variantConverter.Convert(variantLevel.OnHand, variantLevel.UnitId, parent.DefaultUnitId);

            decimal? convertedOnHand = null;
            if (converted.IsSuccess)
            {
                convertedOnHand = converted.Value;
                sum += converted.Value;
            }
            else
            {
                unconverted.Add(variant.Id);
            }

            contributions.Add(new OnHandVariantContribution(
                variant.Id, variant.Name, variantLevel.OnHand, variantLevel.UnitId, variantLevel.UnitCode, convertedOnHand));
        }

        return new OnHandLevel(
            parent.Id, sum, parent.DefaultUnitId, parent.DefaultUnitCode,
            IsParent: true, Variants: contributions, UnconvertedVariantIds: unconverted);
    }

    /// <summary>Everything <see cref="LoadAsync"/> resolves, threaded through the leaf/parent builders.</summary>
    private sealed record RollupContext(
        Dictionary<Guid, ProductStock> StockByProduct,
        Dictionary<Guid, CatalogProductInfo> ProductsById,
        Dictionary<Guid, List<CatalogProductInfo>> VariantsByParent,
        IReadOnlyDictionary<Guid, IQuantityConverter> ConvertersByProduct,
        IReadOnlyDictionary<Guid, string> UnitCodes);

    /// <summary>A leaf's own on-hand — exactly <see cref="InventoryQueryService.DisplayQuantity"/>'s
    /// result, plus the unit id that quantity is actually expressed in (that helper returns only the
    /// unit code; a variant contribution needs the id too, to convert into the parent's unit).</summary>
    private static OnHandLevel LeafLevel(
        CatalogProductInfo product, ProductStock stock,
        IReadOnlyDictionary<Guid, IQuantityConverter> convertersByProduct,
        IReadOnlyDictionary<Guid, string> unitCodes)
    {
        var converter = convertersByProduct[product.Id];
        var activeLots = stock.ActiveLotsFefo().ToList();
        var (total, unitCode) = InventoryQueryService.DisplayQuantity(
            activeLots, product.DefaultUnitId, product.DefaultUnitCode, converter, unitCodes);

        // DisplayQuantity's normal path expresses the total in the product's own default unit; its
        // fallback path (conversion to the default unit failed entirely) expresses it in the lots' own
        // unit instead — recover that unit's id the same way DisplayQuantity itself selected the code.
        var unitId = unitCode == product.DefaultUnitCode
            ? product.DefaultUnitId
            : activeLots.Select(l => l.UnitId).Distinct().ToList() is [var onlyId] ? onlyId : Guid.Empty;

        return new OnHandLevel(product.Id, total, unitId, unitCode, IsParent: false, Variants: [], UnconvertedVariantIds: []);
    }
}
