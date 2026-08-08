namespace Plantry.Pantry.Application;

/// <summary>
/// Narrow read port onto the household's default storage location
/// (<c>HouseholdInventorySettings.DefaultLocationId</c>, plantry-iypo). Lets
/// <c>InventoryProducerAdapter.ProduceAsync</c> (Plantry.Composition/Recipes) fall back to a
/// household-configured storage location — the middle rung between a yielded product's own
/// <c>Product.DefaultLocationId</c> and the arbitrary alphabetically-first active location — without
/// reaching into <see cref="HouseholdDefaultLocationService"/>'s full read/write surface (which also
/// needs <c>ILocationRepository</c> to validate a write). Implemented by
/// <see cref="HouseholdDefaultLocationService"/> itself; both live in <c>Plantry.Pantry.Application</c>
/// (no cross-context ACL indirection needed — <c>Location</c> lives in this same assembly, ADR-024).
/// </summary>
public interface IHouseholdDefaultLocationReader
{
    /// <summary>
    /// The current household's configured default storage location id, or null when there is no
    /// household in context, no persisted settings row, or the household has not configured one. The
    /// caller is responsible for checking it still resolves to an <em>active</em> location — a household
    /// default can point at a since-archived location, same as a product default can.
    /// </summary>
    Task<Guid?> GetDefaultLocationIdAsync(CancellationToken ct = default);
}
