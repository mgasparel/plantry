using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Infrastructure;

public sealed class UserPreferenceRepository(MealPlanningDbContext db) : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        db.UserPreferences
          .Include(up => up.TagStances)
          .SingleOrDefaultAsync(up => up.UserId == userId, ct);

    public async Task AddAsync(UserPreference preference, CancellationToken ct = default) =>
        await db.UserPreferences.AddAsync(preference, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
