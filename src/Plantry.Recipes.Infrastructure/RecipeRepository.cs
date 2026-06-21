using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Infrastructure;

public sealed class RecipeRepository(RecipesDbContext db) : IRecipeRepository
{
    public async Task AddAsync(Recipe recipe, CancellationToken ct = default) =>
        await db.Recipes.AddAsync(recipe, ct);

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
        db.Recipes
            .Include(r => r.Ingredients)
            .Include(r => r.Tags)
            .Include(r => r.Photo)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        db.Recipes.AnyAsync(r => r.HouseholdId == householdId && r.Name == name, ct);

    public async Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        await db.Recipes
            .Include(r => r.Ingredients)
            .Include(r => r.Tags)
            .Where(r => r.ArchivedAt == null)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.Recipes.AnyAsync(r => r.HouseholdId == householdId && r.ArchivedAt == null, ct);
}
