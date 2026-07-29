using Plantry.Catalog.Domain;

namespace Plantry.Web.Inventory;

/// <summary>
/// Per-request cache over <see cref="IUnitRepository"/>'s unit codes (plantry-47tc, plantry-hw39 code
/// review), mirroring <see cref="HouseholdExpiryDefaultsAccessor"/>. <see cref="CatalogReadFacade"/>'s
/// <c>FindProductAsync</c> loaded the whole units table on every call, and
/// <c>InventoryStockReaderAdapter</c> (Plantry.Composition/Recipes) already calls
/// <c>FindProductAsync</c> in a per-product loop, so without this cache a recipe/meal-plan fulfilment
/// read issues one <c>units</c> SELECT per product. Registered scoped, so one instance (and one cached
/// value) lives for the lifetime of the request. Codes-only: <see cref="CatalogReadFacade"/>'s <c>ToInfo</c>
/// consumes only <c>Unit.Code</c> from its units lookup, so a code-keyed dictionary is sufficient.
/// </summary>
public sealed class UnitCodesAccessor(IUnitRepository units)
{
    private IReadOnlyDictionary<Guid, string>? _cached;

    /// <summary>
    /// The household's unit id → code map, resolved once per request and cached.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<Guid, string>> GetCodesAsync(CancellationToken ct = default) =>
        _cached ??= (await units.ListAsync(ct)).ToDictionary(u => u.Id.Value, u => u.Code);
}
