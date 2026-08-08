using Microsoft.Extensions.Logging;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Pantry.Application;

/// <summary>
/// Reads and writes the household's default storage location (plantry-iypo) — the middle rung in
/// <c>InventoryProducerAdapter.ProduceAsync</c>'s yield-placement fallback chain, between a yielded
/// product's own <c>Product.DefaultLocationId</c> and the alphabetically-first active location. Lives on
/// the same <see cref="HouseholdInventorySettings"/> per-household row as
/// <see cref="ExpiringSoonSettingsService"/>'s "expiring soon" horizon — load-or-create on write,
/// falling back to "unset" (null) on read when there is no household in context or no persisted row.
///
/// <para>Unlike the day-count settings, the write path here validates against
/// <see cref="ILocationRepository"/> (an active location belonging to the household) rather than a
/// numeric range — the same existence/active-location check <c>SetDefaultLocationCommand</c> already
/// applies to a product's default location.</para>
/// </summary>
public sealed class HouseholdDefaultLocationService(
    IHouseholdInventorySettingsRepository settings,
    ILocationRepository locations,
    ITenantContext tenant,
    ILogger<HouseholdDefaultLocationService> logger) : IHouseholdDefaultLocationReader
{
    /// <inheritdoc />
    public async Task<Guid?> GetDefaultLocationIdAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return null;

        var record = await settings.FindByHouseholdAsync(HouseholdId.From(householdId), ct);
        return record?.DefaultLocationId?.Value;
    }

    /// <summary>
    /// Validates a candidate default-location id without writing anything: non-null is only valid when
    /// it resolves to an existing, non-archived location; null (clearing the default) always passes.
    /// The single source of truth for this check — <see cref="SetDefaultLocationAsync"/> calls it first
    /// and short-circuits on failure, and <c>PantryModel.OnPostAsync</c> (the /Settings/Pantry page)
    /// calls it directly to validate the location before writing either of the page's two settings, so a
    /// half-applied POST (valid horizon persisted, invalid location silently dropped) can't happen —
    /// without duplicating this check, its rejection message, or its logging as a second copy on the page.
    /// </summary>
    public async Task<Result> ValidateLocationAsync(Guid? locationId, CancellationToken ct = default)
    {
        if (locationId is not { } id)
            return Result.Success();

        var location = await locations.FindAsync(LocationId.From(id), ct);
        if (location is null || location.IsArchived)
        {
            logger.LogWarning(
                "SetDefaultLocation rejected — location {LocationId} does not exist or is archived.", id);
            return UnknownLocationError;
        }

        return Result.Success();
    }

    /// <summary>
    /// Persists the household's default storage location (or clears it, passing null). Creates the
    /// settings row on first write, mirroring <c>ExpiringSoonSettingsService.SetDaysAsync</c>. Returns
    /// a validation error when there is no household in context, or when a non-null id does not resolve
    /// to an active location for this household (see <see cref="ValidateLocationAsync"/>).
    /// </summary>
    public async Task<Result> SetDefaultLocationAsync(Guid? locationId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return Error.Unauthorized;

        var validation = await ValidateLocationAsync(locationId, ct);
        if (validation.IsFailure)
            return validation;

        var householdId = HouseholdId.From(householdGuid);
        var record = await settings.FindByHouseholdAsync(householdId, ct);
        if (record is null)
        {
            record = HouseholdInventorySettings.Create(householdId);
            record.SetDefaultLocationId(locationId is { } lid ? LocationId.From(lid) : null);
            await settings.AddAsync(record, ct);
        }
        else
        {
            record.SetDefaultLocationId(locationId is { } lid ? LocationId.From(lid) : null);
        }

        await settings.SaveChangesAsync(ct);
        logger.LogInformation(
            "Default storage location set to {LocationId} for household {HouseholdId}.",
            locationId, householdId.Value);

        return Result.Success();
    }

    private static readonly Error UnknownLocationError = Error.Custom(
        "Inventory.UnknownDefaultLocation", "Choose an active storage location.");
}
