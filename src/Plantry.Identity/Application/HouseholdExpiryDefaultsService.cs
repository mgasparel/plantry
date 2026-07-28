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
    /// Persists both the after-freezing and after-thawing defaults in one load/mutate/save
    /// (plantry-hw39, absorbing plantry-6nqw) — the single write path for /Settings/Expiry. Both values
    /// are range-validated up front, before the household is loaded, so an out-of-range value never
    /// touches the database. Returns the persisted (AfterFreezing, AfterThawing) tuple on success — since
    /// neither mutator normalizes its input, this is exactly <paramref name="afterFreezing"/>/
    /// <paramref name="afterThawing"/> echoed back, letting a caller (e.g. <c>ExpiryModel.OnPostAsync</c>)
    /// reflect the new state without an extra <see cref="GetAsync"/> round trip.
    /// </summary>
    public async Task<Result<(int AfterFreezing, int AfterThawing)>> SetAllAsync(
        int afterFreezing, int afterThawing, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetAllAsync rejected — no household in context.");
            return Error.Unauthorized;
        }

        if (afterFreezing < MinDays || afterFreezing > MaxDays || afterThawing < MinDays || afterThawing > MaxDays)
        {
            logger.LogWarning(
                "SetAllAsync rejected — AfterFreezing={AfterFreezing} AfterThawing={AfterThawing} outside [{Min}, {Max}].",
                afterFreezing, afterThawing, MinDays, MaxDays);
            return DaysOutOfRangeError;
        }

        var householdId = HouseholdId.From(householdGuid);
        var household = await households.FindAsync(householdId, ct);
        if (household is null)
        {
            logger.LogWarning("SetAllAsync rejected — household {HouseholdId} not found.", householdId.Value);
            return Error.NotFound;
        }

        household.SetDefaultDueDaysAfterFreezing(afterFreezing);
        household.SetDefaultDueDaysAfterThawing(afterThawing);
        await households.SaveChangesAsync(ct);
        logger.LogInformation(
            "Default expiry days set to AfterFreezing={AfterFreezing} AfterThawing={AfterThawing} for household {HouseholdId}.",
            afterFreezing, afterThawing, householdId.Value);

        return (afterFreezing, afterThawing);
    }

    private static readonly Error DaysOutOfRangeError = Error.Custom(
        "Identity.InvalidExpiryDefaultDays", $"Choose between {MinDays} and {MaxDays} days.");
}
