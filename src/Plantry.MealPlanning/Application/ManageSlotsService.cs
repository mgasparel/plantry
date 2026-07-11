using Microsoft.Extensions.Logging;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service for managing a household's meal slot configuration (P3-1).
/// Orchestrates domain mutations on <see cref="MealSlotConfig"/> and delegates
/// identity reads to the <see cref="IHouseholdMemberReader"/> port.
/// </summary>
public sealed class ManageSlotsService(
    IMealSlotConfigRepository repository,
    IHouseholdMemberReader memberReader,
    IClock clock,
    ILogger<ManageSlotsService> logger)
{
    /// <summary>Returns the slot config for the household, or null if none exists yet.</summary>
    public Task<MealSlotConfig?> GetSlotsAsync(HouseholdId householdId, CancellationToken ct = default)
        => repository.FindByHouseholdAsync(householdId, ct);

    /// <summary>Returns the signed-in household's members from the identity port.</summary>
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default)
        => memberReader.ListMembersAsync(ct);

    /// <summary>Adds a new active slot to the household config. Creates the config if it does not exist.</summary>
    public async Task AddSlotAsync(HouseholdId householdId, string label, CancellationToken ct = default)
    {
        var config = await repository.FindByHouseholdAsync(householdId, ct);
        if (config is null)
        {
            config = MealSlotConfig.CreateWithDefaults(householdId, clock);
            await repository.AddAsync(config, ct);
        }

        config.AddSlot(label, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation("Meal slot '{Label}' added for household {HouseholdId}.", label, householdId.Value);
    }

    /// <summary>Renames an active slot.</summary>
    public async Task RenameSlotAsync(
        HouseholdId householdId,
        MealSlotId slotId,
        string newLabel,
        CancellationToken ct = default)
    {
        var config = await RequireConfigAsync(householdId, ct);
        config.RenameSlot(slotId, newLabel, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation("Meal slot {SlotId} renamed for household {HouseholdId}.", slotId.Value, householdId.Value);
    }

    /// <summary>Reorders active slots to match the supplied ordered list of IDs.</summary>
    public async Task ReorderSlotsAsync(
        HouseholdId householdId,
        IReadOnlyList<MealSlotId> orderedIds,
        CancellationToken ct = default)
    {
        var config = await RequireConfigAsync(householdId, ct);
        config.ReorderSlots(orderedIds, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation("Meal slots reordered for household {HouseholdId}.", householdId.Value);
    }

    /// <summary>Sets the default attendees on an active slot.</summary>
    public async Task SetDefaultAttendeesAsync(
        HouseholdId householdId,
        MealSlotId slotId,
        IReadOnlyList<Guid> memberIds,
        CancellationToken ct = default)
    {
        var config = await RequireConfigAsync(householdId, ct);
        config.SetDefaultAttendees(slotId, memberIds, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation("Default attendees set for meal slot {SlotId} in household {HouseholdId}.", slotId.Value, householdId.Value);
    }

    /// <summary>Sets whether an active slot participates in bulk AI auto-planning (plantry-av8z).</summary>
    public async Task SetAutoPlanEnabledAsync(
        HouseholdId householdId,
        MealSlotId slotId,
        bool enabled,
        CancellationToken ct = default)
    {
        var config = await RequireConfigAsync(householdId, ct);
        config.SetAutoPlanEnabled(slotId, enabled, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation(
            "Auto-plan {State} for meal slot {SlotId} in household {HouseholdId}.",
            enabled ? "enabled" : "disabled", slotId.Value, householdId.Value);
    }

    /// <summary>Soft-archives an active slot; renumbers remaining active ordinals.</summary>
    public async Task ArchiveSlotAsync(
        HouseholdId householdId,
        MealSlotId slotId,
        CancellationToken ct = default)
    {
        var config = await RequireConfigAsync(householdId, ct);
        config.ArchiveSlot(slotId, clock);
        await repository.SaveChangesAsync(ct);
        logger.LogInformation("Meal slot {SlotId} archived for household {HouseholdId}.", slotId.Value, householdId.Value);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private async Task<MealSlotConfig> RequireConfigAsync(HouseholdId householdId, CancellationToken ct)
    {
        var config = await repository.FindByHouseholdAsync(householdId, ct);
        if (config is null)
        {
            logger.LogWarning(
                "RequireConfig failed — no meal slot config for household {HouseholdId}.", householdId.Value);
            throw new InvalidOperationException(
                $"No meal slot config found for household '{householdId}'.");
        }
        return config;
    }
}
