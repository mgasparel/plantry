namespace Plantry.Identity.Application;

/// <summary>
/// The single point of truth for the current household's freeze/thaw expiry defaults (plantry-hh1f) —
/// the due-days <c>TransferStockCommand</c> falls back to when a product carries no per-product
/// after-freezing/after-thawing override.
///
/// Reads fall back to <see cref="HouseholdExpiryDefaultsService.DefaultAfterFreezing"/>/
/// <see cref="HouseholdExpiryDefaultsService.DefaultAfterThawing"/> (90/3) when there is no household
/// in context or no persisted row. Sibling contexts consume this through their own ACL reader port + a
/// Composition adapter (mirroring the <c>IExpiringSoonHorizon</c> pattern), so no downstream context
/// takes a hard dependency on Identity. The write path (the future /Settings/Expiry page, plantry-qckx)
/// lives on <see cref="HouseholdExpiryDefaultsService"/>.
/// </summary>
public interface IHouseholdExpiryDefaults
{
    /// <summary>
    /// The current household's (after-freezing, after-thawing) due-days defaults. Returns the
    /// <see cref="HouseholdExpiryDefaultsService"/> fallback (90, 3) when there is no household in
    /// context or no persisted household row yet.
    /// </summary>
    Task<(int AfterFreezing, int AfterThawing)> GetAsync(CancellationToken ct = default);
}
