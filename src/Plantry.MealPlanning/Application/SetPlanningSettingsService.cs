using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Application service: persists planning settings as a per-week override
/// (or the household default when <paramref name="weekStart"/> is null).
/// Returns the resolved effective settings after the upsert so the caller can
/// immediately pass them to insights/UI without a second round-trip.
/// </summary>
public sealed class SetPlanningSettingsService(
    IHouseholdPlanningSettingsRepository settingsRepo,
    IWeekPlanningOverrideRepository overrideRepo)
{
    /// <summary>
    /// Upserts a per-week override for the given household and week.
    /// When <paramref name="weekStart"/> is provided, persists the week override;
    /// otherwise updates the household default.
    /// Returns the resolved (Budget, Weights) after the write.
    /// </summary>
    public async Task<(Money? Budget, PlanningWeights? Weights)> ExecuteAsync(
        HouseholdId householdId,
        DateOnly? weekStart,
        Money? budget,
        PlanningWeights? weights,
        CancellationToken ct = default)
    {
        var settings = await settingsRepo.FindByHouseholdAsync(householdId, ct);

        if (weekStart.HasValue)
        {
            // Upsert the per-week override.
            var existing = await overrideRepo.FindAsync(householdId, weekStart.Value, ct);
            if (existing is null)
            {
                var newOverride = WeekPlanningOverride.Create(householdId, weekStart.Value);
                newOverride.Set(budget, weights);
                await overrideRepo.AddAsync(newOverride, ct);
                await overrideRepo.SaveChangesAsync(ct);

                return PlanningSettingsResolver.Resolve(settings, newOverride);
            }
            else
            {
                existing.Set(budget, weights);
                await overrideRepo.SaveChangesAsync(ct);

                return PlanningSettingsResolver.Resolve(settings, existing);
            }
        }
        else
        {
            // Update the household default.
            if (settings is null)
            {
                settings = HouseholdPlanningSettings.Create(householdId);
                settings.SetDefaults(budget, weights);
                await settingsRepo.AddAsync(settings, ct);
            }
            else
            {
                settings.SetDefaults(budget, weights);
            }

            await settingsRepo.SaveChangesAsync(ct);
            return PlanningSettingsResolver.Resolve(settings, weekOverride: null);
        }
    }
}
