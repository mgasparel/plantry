using Plantry.Identity.Application;

namespace Plantry.Web.Inventory;

/// <summary>
/// Per-request cache over <see cref="IHouseholdExpiryDefaults"/> (plantry-hw39, absorbing plantry-rsy1),
/// mirroring <c>DisplayCurrencyAccessor</c> (<c>src/Plantry.Composition/Identity/DisplayCurrencyAccessor.cs</c>).
/// <see cref="HouseholdExpiryDefaultsReaderAdapter"/> reads through this instead of calling
/// <see cref="IHouseholdExpiryDefaults.GetAsync"/> directly — <see cref="CatalogReadFacade"/>'s
/// <c>FindProductAsync</c> resolves the household's freeze/thaw defaults on every call, and
/// <c>InventoryStockReaderAdapter</c> (Plantry.Composition/Recipes) already calls
/// <c>FindProductAsync</c> in a per-product loop, so without this cache a recipe/meal-plan
/// fulfilment read issues one extra <c>households</c> SELECT per product. Registered scoped, so one
/// instance (and one cached value) lives for the lifetime of the request — same "one household read
/// per request" fix <c>DisplayCurrencyAccessor</c> already applies to <see cref="IDisplayCurrency"/>.
/// </summary>
public sealed class HouseholdExpiryDefaultsAccessor(IHouseholdExpiryDefaults source)
{
    private (int AfterFreezing, int AfterThawing)? _cached;

    /// <summary>
    /// The current household's (after-freezing, after-thawing) due-days defaults, resolved once per
    /// request and cached. Falls back to <see cref="HouseholdExpiryDefaultsService.DefaultAfterFreezing"/>/
    /// <see cref="HouseholdExpiryDefaultsService.DefaultAfterThawing"/> (90/3) via the underlying service
    /// when there is no household in context or no persisted row.
    /// </summary>
    public async ValueTask<(int AfterFreezing, int AfterThawing)> GetAsync(CancellationToken ct = default) =>
        _cached ??= await source.GetAsync(ct);
}
