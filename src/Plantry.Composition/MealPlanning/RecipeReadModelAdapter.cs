using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Application;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IRecipeReadModel"/> — supplies the MealPlanning context with
/// recipe display facts from the Recipes context, over <see cref="RecipesDbContext"/>.
/// Also computes live fulfillment/cost enrichment by invoking Recipes' domain services
/// (<see cref="FulfillmentService"/> / <see cref="CostingService"/>) — MealPlanning borrows
/// these computations and rolls them up, never reimplements them (domain-model §1).
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Recipes dependencies.
/// </summary>
public sealed class RecipeReadModelAdapter(
    RecipesDbContext db,
    FulfillmentService fulfillmentService,
    CostingService costingService) : IRecipeReadModel
{
    public async Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        // Use the strongly-typed RecipeId so EF Core's value converter can translate the predicate.
        // Accessing .Value directly on a converted type in a LINQ predicate causes a translation
        // failure when combined with a HasQueryFilter that also uses a converted type.
        var rid = RecipeId.From(recipeId);
        // Project rather than load the entity: `r.Photo != null` becomes an EXISTS/JOIN, so the
        // (potentially large) photo bytes are never hydrated just to report presence.
        var row = await db.Recipes
            .Where(r => r.Id == rid && r.ArchivedAt == null)
            .Select(r => new
            {
                r.Id,
                r.Name,
                TagIds = r.Tags.Select(t => t.TagId.Value).ToList(),
                r.DefaultServings,
                HasPhoto = r.Photo != null,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new RecipeReadModel(row.Id.Value, row.Name, row.TagIds, row.DefaultServings, row.HasPhoto);
    }

    public async Task<IReadOnlyList<RecipeReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(nameQuery) ? "" : nameQuery.Trim();

        var rows = await db.Recipes
            .Where(r => r.ArchivedAt == null &&
                        (q == "" || EF.Functions.ILike(r.Name, $"%{q}%")))
            .OrderBy(r => r.Name)
            .Take(maxResults)
            .Select(r => new
            {
                r.Id,
                r.Name,
                TagIds = r.Tags.Select(t => t.TagId.Value).ToList(),
                r.DefaultServings,
                HasPhoto = r.Photo != null,
            })
            .ToListAsync(ct);

        return rows.Select(r => new RecipeReadModel(
            r.Id.Value, r.Name, r.TagIds, r.DefaultServings, r.HasPhoto)).ToList();
    }

    /// <inheritdoc />
    public async Task<RecipeDishEnrichment?> GetEnrichmentAsync(
        Guid recipeId,
        int servings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var rid = RecipeId.From(recipeId);
        var recipe = await db.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ArchivedAt == null, ct);

        if (recipe is null) return null;

        // Borrow Recipes' domain services — MealPlanning rolls up, never recomputes.
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, servings, today, ct);
        var cost = await costingService.ComputeAsync(recipe, servings, ct);

        // Compute fulfillment % from the ingredient-level results.
        // Untracked staples are excluded (always satisfied, C12). Only tracked lines contribute.
        var trackedLines = fulfillment.Lines
            .Where(l => l.Status != IngredientStatus.Untracked)
            .ToList();

        int pct;
        if (trackedLines.Count == 0)
        {
            // No tracked ingredients → treat as 100% (untracked-only recipe is always cookable).
            pct = 100;
        }
        else
        {
            var inStockCount = trackedLines.Count(l => l.Status == IngredientStatus.InStock);
            pct = (int)Math.Round(100.0 * inStockCount / trackedLines.Count);
        }

        var hasExpiring = fulfillment.Lines
            .Any(l => l.ExpiresWithinDays.HasValue);

        // TotalCost = CostPerServing.Amount × servings (Amount is per-serving; we want the total).
        decimal? totalCost = cost.Amount.HasValue ? cost.Amount.Value * servings : null;

        return new RecipeDishEnrichment(
            pct,
            totalCost,
            cost.Completeness == CostCompleteness.Partial,
            hasExpiring);
    }

    /// <inheritdoc />
    public async Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
    {
        // Targeted full-corpus query: does ANY non-archived recipe carry this tag?
        // Never filtered by the 50-cap candidate list from SearchAsync.
        var tid = TagId.From(tagId);
        return await db.Recipes
            .Where(r => r.ArchivedAt == null)
            .AnyAsync(r => r.Tags.Any(t => t.TagId == tid), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(
        Guid recipeId,
        int servings,
        CancellationToken ct = default)
    {
        var rid = RecipeId.From(recipeId);
        var recipe = await db.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ArchivedAt == null, ct);

        if (recipe is null) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, servings, today, ct);

        // Delegate to the shared shortfall calculator (Missing + Low, shortfall = scaledRequired − available)
        // so this path and AddMissingToShoppingList (J5) cannot diverge.
        var shortfallLines = RecipeShortfallCalculator.Compute(recipe, fulfillment, servings);

        return shortfallLines
            .Select(s => new RecipeMissingIngredient(s.ProductId, s.ShortfallQuantity, s.UnitId))
            .ToList();
    }
}
