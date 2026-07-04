using Microsoft.Extensions.Logging;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Inventory.Application;

/// <summary>
/// Reads and writes the per-household "expiring soon" horizon, and serves as the
/// <see cref="IExpiringSoonHorizon"/> read source for every consumer. Mirrors the load-or-create
/// upsert used by the other single-per-household settings aggregates: a household with no row yet
/// falls back to <see cref="HouseholdInventorySettings.DefaultExpiringSoonDays"/> on read, and a row
/// is seeded lazily on first write.
/// </summary>
public sealed class ExpiringSoonSettingsService(
    IHouseholdInventorySettingsRepository settings,
    ITenantContext tenant,
    ILogger<ExpiringSoonSettingsService> logger) : IExpiringSoonHorizon
{
    /// <inheritdoc />
    public async Task<int> GetDaysAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return HouseholdInventorySettings.DefaultExpiringSoonDays;

        var record = await settings.FindByHouseholdAsync(HouseholdId.From(householdId), ct);
        return record?.ExpiringSoonDays ?? HouseholdInventorySettings.DefaultExpiringSoonDays;
    }

    /// <summary>
    /// Persists the household's "expiring soon" horizon (in days). Creates the settings row on first
    /// write. Returns a validation error when there is no household in context or the value is outside
    /// the accepted range.
    /// </summary>
    public async Task<Result> SetDaysAsync(int days, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return Error.Unauthorized;

        if (days < HouseholdInventorySettings.MinExpiringSoonDays ||
            days > HouseholdInventorySettings.MaxExpiringSoonDays)
        {
            logger.LogWarning(
                "SetExpiringSoonDays rejected — {Days} is outside [{Min}, {Max}].",
                days, HouseholdInventorySettings.MinExpiringSoonDays, HouseholdInventorySettings.MaxExpiringSoonDays);
            return Error.Custom(
                "Inventory.InvalidExpiringSoonDays",
                $"Choose between {HouseholdInventorySettings.MinExpiringSoonDays} and {HouseholdInventorySettings.MaxExpiringSoonDays} days.");
        }

        var householdId = HouseholdId.From(householdGuid);
        var record = await settings.FindByHouseholdAsync(householdId, ct);
        if (record is null)
        {
            record = HouseholdInventorySettings.Create(householdId);
            record.SetExpiringSoonDays(days);
            await settings.AddAsync(record, ct);
        }
        else
        {
            record.SetExpiringSoonDays(days);
        }

        await settings.SaveChangesAsync(ct);
        logger.LogInformation(
            "Expiring-soon horizon set to {Days} days for household {HouseholdId}.",
            days, householdId.Value);

        return Result.Success();
    }
}
