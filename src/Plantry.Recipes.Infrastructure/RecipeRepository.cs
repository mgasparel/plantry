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

    public async Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default)
    {
        // Select only the PK column (recipe_id) from recipe_photo — no bytea loaded.
        // RLS query filter on RecipePhoto scopes to the current household automatically.
        var ids = await db.RecipePhotos
            .Select(p => p.Id)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        db.Recipes.AnyAsync(r => r.HouseholdId == householdId && r.ArchivedAt == null, ct);

    public async Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<RecipeId, string>();

        // Lightweight name-projection query with no navigation includes (no Ingredients/Tags/Photo).
        // The RLS interceptor scopes the query to the current household automatically (ADR-008).
        // EF translates the Contains against the value-converted Id column as a SQL IN (...) clause.
        var wanted = ids.ToHashSet();
        var results = await db.Recipes
            .Where(r => wanted.Contains(r.Id) && r.ArchivedAt == null)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);

        return results.ToDictionary(r => r.Id, r => r.Name);
    }
}
