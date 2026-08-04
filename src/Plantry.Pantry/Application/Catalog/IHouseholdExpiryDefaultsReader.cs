namespace Plantry.Pantry.Application;

/// <summary>
/// Anti-corruption read port onto the household's freeze/thaw expiry defaults (owned by Identity,
/// <c>Household.DefaultDueDaysAfterFreezing</c>/<c>DefaultDueDaysAfterThawing</c>, plantry-hh1f). Lets
/// <see cref="Domain.ExpiryDefaultResolver"/>'s freeze/thaw fallback resolve against the household-wide
/// default without coupling Catalog to the Identity domain model or its EF context (ADR-002, Gate 2).
/// Defined here in Catalog.Application and <b>implemented in Plantry.Composition</b> over Identity's
/// <c>IHouseholdExpiryDefaults</c>, so the Catalog project keeps its <c>→ SharedKernel only</c>
/// dependency — mirroring <c>IAiAssistanceGateReader</c>/<c>IExpiringSoonHorizonReader</c>.
/// </summary>
public interface IHouseholdExpiryDefaultsReader
{
    /// <summary>
    /// The current household's after-freezing/after-thawing due-days defaults. Falls back to the
    /// aggregate defaults (90/3) when there is no household in context or no persisted row yet,
    /// matching <c>Household</c>'s own property defaults.
    /// </summary>
    Task<(int AfterFreezing, int AfterThawing)> GetDefaultsAsync(CancellationToken ct = default);
}
