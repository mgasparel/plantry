using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanStockReader"/> — delegates to the same Inventory
/// repositories and converters used by the Recipes <c>InventoryStockReaderAdapter</c>.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Inventory dependencies.
/// All identifiers cross as raw <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public sealed class MealPlanStockReaderAdapter(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
    ITenantContext tenant)
    : IMealPlanStockReader
{
    public async Task<MealPlanProductStock?> FindStockAsync(
        Guid productId,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return null;

        var householdId = HouseholdId.From(householdGuid);

        // Resolve catalog info first — the product must exist in Catalog to be trackable.
        // We need this for DefaultUnitId regardless of whether there are active lots.
        var catalogInfo = await catalog.FindProductAsync(productId, ct);
        if (catalogInfo is null) return null; // unknown product — not trackable

        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        var productStock = allStock.FirstOrDefault(s => s.ProductId == productId);

        // Never-stocked or fully-consumed: return zero-qty snapshot so ShopForWeekService
        // can still resolve the DefaultUnitId and add the product to the shopping list.
        // Returning null here would cause the Guid.Empty guard in ShopForWeekService to
        // silently drop the product dish (the maximally-short case must reach the list).
        if (productStock is null || !productStock.ActiveLotsFefo().Any())
        {
            return new MealPlanProductStock(productId, 0m, catalogInfo.DefaultUnitId, null);
        }

        var activeLots = productStock.ActiveLotsFefo().ToList();
        var converter = await conversions.ForProductAsync(productId, ct);

        // Aggregate quantity into the product's default unit.
        var total = 0m;
        foreach (var lot in activeLots)
        {
            var converted = converter.Convert(lot.Quantity, lot.UnitId, catalogInfo.DefaultUnitId);
            if (converted.IsSuccess)
                total += converted.Value;
        }

        DateOnly? soonestExpiry = activeLots
            .Where(l => l.ExpiryDate.HasValue)
            .Select(l => l.ExpiryDate!.Value)
            .Cast<DateOnly?>()
            .DefaultIfEmpty(null)
            .Min();

        return new MealPlanProductStock(
            productId,
            total,
            catalogInfo.DefaultUnitId,
            soonestExpiry);
    }
}
