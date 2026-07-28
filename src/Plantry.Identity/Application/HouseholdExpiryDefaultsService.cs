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
///
/// <para>
/// Write validation (plantry-qckx): both fields are bounded to [<see cref="MinDays"/>,
/// <see cref="MaxDays"/>] = [0, 3650] (10 years), checked here and reported as
/// <see cref="Error.Custom(string, string)"/> rather than left to the aggregate's non-negative-only
/// <see cref="ArgumentOutOfRangeException"/> guard — mirroring <c>ExpiringSoonSettingsService.SetDaysAsync</c>,
/// the nearest same-shape household setting. The upper bound exists because
/// <c>ProductStock.Transfer</c> computes <c>today.AddDays(days)</c>: an unbounded value risks
/// <see cref="ArgumentOutOfRangeException"/> once the result passes year 9999, and mirrors the bound
/// already enforced on the equivalent per-product override fields
/// (<c>Pages/Catalog/Products/Detail.cshtml.cs</c>'s <c>[Range(0, 3650)]</c> on
/// <c>DefaultDueDaysAfterFreezing</c>/<c>DefaultDueDaysAfterThawing</c>) — a household default and a
/// per-product override feed the identical computation, so they share the identical bound.
/// </para>
///
/// <para>
/// The household previously carried a separate, never-consumed "expiry warning days" field
/// (a per-row column, since retired via a generated EF migration, plantry-qckx) that duplicated
/// <c>HouseholdInventorySettings.ExpiringSoonDays</c> (the Inventory context's live "expiring soon"
/// horizon, already user-editable at <c>/Settings/Pantry</c> via <c>ExpiringSoonSettingsService</c>).
/// This service does not, and never will, re-add that field. One household-wide "expiring soon"
/// number, one place to set it.
/// </para>
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

    /// <summary>Minimum accepted value for either day-count field this service writes.</summary>
    public const int MinDays = 0;

    /// <summary>
    /// Maximum accepted value for either day-count field this service writes (10 years) —
    /// see the type-level remarks for why.
    /// </summary>
    public const int MaxDays = 3650;

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
    /// context (unauthorized), the household row cannot be found, or <paramref name="days"/> falls
    /// outside [<see cref="MinDays"/>, <see cref="MaxDays"/>] — validated here (not left to the
    /// aggregate's non-negative-only guard) so an out-of-range value comes back as a reportable
    /// <see cref="Error.Custom(string, string)"/> instead of an exception, mirroring
    /// <c>ExpiringSoonSettingsService.SetDaysAsync</c>.
    /// </summary>
    public async Task<Result> SetAfterFreezingAsync(int days, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetDefaultDueDaysAfterFreezing rejected — no household in context.");
            return Error.Unauthorized;
        }

        if (days < MinDays || days > MaxDays)
        {
            logger.LogWarning(
                "SetDefaultDueDaysAfterFreezing rejected — {Days} is outside [{Min}, {Max}].", days, MinDays, MaxDays);
            return DaysOutOfRangeError;
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

        if (days < MinDays || days > MaxDays)
        {
            logger.LogWarning(
                "SetDefaultDueDaysAfterThawing rejected — {Days} is outside [{Min}, {Max}].", days, MinDays, MaxDays);
            return DaysOutOfRangeError;
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

    private static readonly Error DaysOutOfRangeError = Error.Custom(
        "Identity.InvalidExpiryDefaultDays", $"Choose between {MinDays} and {MaxDays} days.");
}
