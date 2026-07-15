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
            .Include(r => r.Inclusions)
            .Include(r => r.Tags)
            .Include(r => r.Photo)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlySet<RecipeId>> ResolveExistingIdsAsync(IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new HashSet<RecipeId>();
        var wanted = ids.ToHashSet();
        // Existence check mirrors GetByIdAsync — RLS-scoped, archived recipes included; PK projection only
        // (no Ingredients/Inclusions/Tags/Photo includes). EF translates Contains to a SQL IN (...) clause.
        var found = await db.Recipes.Where(r => wanted.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        return found.ToHashSet();
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        db.Recipes.AnyAsync(r => r.HouseholdId == householdId && r.Name == name, ct);

    public async Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        await db.Recipes
            .Include(r => r.Ingredients)
            .Include(r => r.Inclusions)
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

    public async Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default)
    {
        // Project only the two id columns — no aggregate materialization. The RLS query filter on
        // Inclusion scopes the edges to the current household automatically (ADR-008).
        var edges = await db.Inclusions
            .Select(i => new { i.RecipeId, i.SubRecipeId })
            .ToListAsync(ct);
        return edges.Select(e => new RecipeInclusionEdge(e.RecipeId, e.SubRecipeId)).ToList();
    }

    public async Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default)
    {
        if (!transitive)
        {
            // Direct includers only — one indexed query (ix_recipe_inclusion_household_sub).
            var direct = await db.Inclusions
                .Where(i => i.SubRecipeId == subRecipeId)
                .Select(i => i.RecipeId)
                .Distinct()
                .ToListAsync(ct);
            return direct.ToHashSet();
        }

        // Transitive includers — walk the reverse (sub → parent) edges in memory. Household recipe counts
        // make loading the full edge set trivially cheap (recipe-composition.md D5).
        var edges = await ListInclusionEdgesAsync(ct);
        var bySub = edges
            .GroupBy(e => e.SubId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ParentId).ToList());

        var result = new HashSet<RecipeId>();
        var queue = new Queue<RecipeId>();
        queue.Enqueue(subRecipeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!bySub.TryGetValue(current, out var parents)) continue;
            foreach (var parent in parents)
            {
                if (result.Add(parent))
                    queue.Enqueue(parent);
            }
        }
        return result;
    }
}
