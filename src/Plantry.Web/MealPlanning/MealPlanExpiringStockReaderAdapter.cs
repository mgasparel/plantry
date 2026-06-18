using Plantry.Inventory.Domain;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IMealPlanExpiringStockReader"/> (P3-5).
/// Delegates to the Inventory repository to find products with expiring stock.
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Inventory dependencies.
/// </summary>
public sealed class MealPlanExpiringStockReaderAdapter(
    IProductStockRepository stocks,
    ITenantContext tenant) : IMealPlanExpiringStockReader
{
    public async Task<IReadOnlyList<Guid>> GetExpiringProductIdsAsync(
        DateOnly today,
        int withinDays,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return [];

        var householdId = HouseholdId.From(householdGuid);
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);

        var cutoff = today.AddDays(withinDays);
        var expiringProductIds = new List<Guid>();

        foreach (var stock in allStock)
        {
            var activeLots = stock.ActiveLotsFefo().ToList();
            if (activeLots.Count == 0) continue;

            // A product is "expiring soon" when its soonest-expiry lot falls within the window.
            var soonest = activeLots
                .Where(l => l.ExpiryDate.HasValue)
                .Select(l => l.ExpiryDate!.Value)
                .Cast<DateOnly?>()
                .DefaultIfEmpty(null)
                .Min();

            if (soonest.HasValue && soonest.Value >= today && soonest.Value <= cutoff)
                expiringProductIds.Add(stock.ProductId);
        }

        return expiringProductIds;
    }
}
