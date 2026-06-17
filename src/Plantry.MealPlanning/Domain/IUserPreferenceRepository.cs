using Plantry.SharedKernel;

namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Persistence port for the <see cref="UserPreference"/> aggregate (mealplanning.md §user_preference, M6).
/// </summary>
public interface IUserPreferenceRepository
{
    /// <summary>
    /// Finds the preference profile for <paramref name="userId"/> within the household.
    /// Returns <c>null</c> when the member has never edited their profile (lazy-create pattern, M6).
    /// The EF query filter / RLS ensures only the signed-in household's rows are visible.
    /// </summary>
    Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Adds a newly-created preference aggregate to the context. Call before <c>SaveChangesAsync</c>.</summary>
    Task AddAsync(UserPreference preference, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
