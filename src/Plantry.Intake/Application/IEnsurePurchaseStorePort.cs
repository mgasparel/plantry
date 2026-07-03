namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Catalog to resolve (find-or-create) a <c>catalog.store</c> identity for a
/// receipt merchant during intake commit (DM-16). Implemented in Plantry.Web as an adapter over Catalog's
/// <c>EnsureStoreByNameCommand</c>, so Intake never touches <c>CatalogDbContext</c> directly (ADR-010).
/// Returns the resolved store id, which is stamped onto the purchase <c>price_observation</c>.
/// </summary>
public interface IEnsurePurchaseStorePort
{
    /// <summary>Ensures a <c>catalog.store</c> exists for the (non-blank) merchant name and returns its id.</summary>
    Task<Guid> EnsureAsync(string merchantName, CancellationToken ct = default);
}
