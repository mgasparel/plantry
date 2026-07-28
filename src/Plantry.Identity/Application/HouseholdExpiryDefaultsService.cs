using Microsoft.Extensions.Logging;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Identity.Application;

/// <summary>
/// Reads and writes the household-wide freeze/thaw expiry defaults (plantry-hh1f), and serves as the
/// <see cref="IHouseholdExpiryDefaults"/> read source for <c>IHouseholdExpiryDefaultsReader</c> (the
/// Catalog ACL that feeds <c>ExpiryDefaultResolver</c>'s freeze/thaw fallback). The settings live on
/// the <see cref="Household"/> aggregate root (like <c>Theme</c> and <c>DisplayCurrency</c>) — one row
/// per household in the <c>identity</c> schema, already the tenant anchor — so no separate settings
/// table or RLS wiring is needed. Falls back to <see cref="DefaultAfterFreezing"/>/
/// <see cref="DefaultAfterThawing"/> (90/3) when unset — the same defaults the aggregate itself carries
/// and the EF migration backfilled onto every pre-existing household.
/// </summary>
public sealed class HouseholdExpiryDefaultsService(
    IHouseholdRepository households,
    ITenantContext tenant,
    ILogger<HouseholdExpiryDefaultsService> logger) : IHouseholdExpiryDefaults
{
    /// <summary>Fallback after-freezing due-days when there is no household in context or no persisted row.</summary>
    public const int DefaultAfterFreezing = 90;

    /// <summary>Fallback after-thawing due-days when there is no household in context or no persisted row.</summary>
    public const int DefaultAfterThawing = 3;

    /// <inheritdoc />
    public async Task<(int AfterFreezing, int AfterThawing)> GetAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return (DefaultAfterFreezing, DefaultAfterThawing);

        var household = await households.FindAsync(HouseholdId.From(householdGuid), ct);
        return household is null
            ? (DefaultAfterFreezing, DefaultAfterThawing)
            : (household.DefaultDueDaysAfterFreezing, household.DefaultDueDaysAfterThawing);
    }

    /// <summary>
    /// Persists the household's after-freezing default. Returns a failure when there is no household in
    /// context (unauthorized) or the household row cannot be found; the aggregate rejects a negative
    /// value with <see cref="ArgumentOutOfRangeException"/>, not caught here — surfaces as the caller's
    /// binding/validation error, mirroring <see cref="DisplayCurrencyService.SetAsync"/>.
    /// </summary>
    public async Task<Result> SetAfterFreezingAsync(int days, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetDefaultDueDaysAfterFreezing rejected — no household in context.");
            return Error.Unauthorized;
        }

        var householdId = HouseholdId.From(householdGuid);
        var household = await households.FindAsync(householdId, ct);
        if (household is null)
        {
            logger.LogWarning(
                "SetDefaultDueDaysAfterFreezing rejected — household {HouseholdId} not found.", householdId.Value);
            return Error.NotFound;
        }

        household.SetDefaultDueDaysAfterFreezing(days);
        await households.SaveChangesAsync(ct);
        logger.LogInformation(
            "Default after-freezing due-days set to {Days} for household {HouseholdId}.", days, householdId.Value);

        return Result.Success();
    }

    /// <summary>Persists the household's after-thawing default. Mirrors <see cref="SetAfterFreezingAsync"/>.</summary>
    public async Task<Result> SetAfterThawingAsync(int days, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetDefaultDueDaysAfterThawing rejected — no household in context.");
            return Error.Unauthorized;
        }

        var householdId = HouseholdId.From(householdGuid);
        var household = await households.FindAsync(householdId, ct);
        if (household is null)
        {
            logger.LogWarning(
                "SetDefaultDueDaysAfterThawing rejected — household {HouseholdId} not found.", householdId.Value);
            return Error.NotFound;
        }

        household.SetDefaultDueDaysAfterThawing(days);
        await households.SaveChangesAsync(ct);
        logger.LogInformation(
            "Default after-thawing due-days set to {Days} for household {HouseholdId}.", days, householdId.Value);

        return Result.Success();
    }
}
