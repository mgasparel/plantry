using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that soft-deletes (archives) a recipe, enforcing the N5 guard: a recipe referenced
/// by ≥ 1 other recipe's inclusion cannot be archived (recipe-composition.md D12/N5). Warn-and-block is the
/// least-surprising v1 rule — auto-flatten-on-archive can come later. The includer count comes from the
/// cross-aggregate <see cref="IRecipeRepository.GetIncluderIdsAsync"/> lookup (household-scoped by RLS), so
/// the aggregate stays free of cross-context reads.
/// </summary>
public sealed class ArchiveRecipe(
    IRecipeRepository recipes,
    IClock clock,
    ILogger<ArchiveRecipe> logger)
{
    public async Task<Result> ExecuteAsync(RecipeId recipeId, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null)
            return Error.NotFound;

        // N5 — block while the recipe is included by others. Direct includers are the set the message counts.
        var includers = await recipes.GetIncluderIdsAsync(recipeId, transitive: false, ct);
        if (includers.Count > 0)
        {
            logger.LogInformation(
                "Archive blocked for recipe {RecipeId}: still included by {IncluderCount} recipe(s).",
                recipeId.Value, includers.Count);
            return Error.Custom(
                "Recipes.IncludedByOthers",
                $"This recipe is used by {includers.Count} recipe{(includers.Count == 1 ? "" : "s")} and cannot be archived.");
        }

        recipe.Archive(clock);
        await recipes.SaveChangesAsync(ct);
        logger.LogInformation("Recipe {RecipeId} archived.", recipeId.Value);
        return Result.Success();
    }
}
