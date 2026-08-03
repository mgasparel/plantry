using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that upserts a household member's star rating on a recipe (plantry-zlwp.1):
/// creates the <see cref="RecipeRating"/> row on first rate, updates <see cref="RecipeRating.Stars"/>
/// after (mirrors <c>SetPreferences.SetStanceAsync</c>'s create-or-update shape). No opinion = absence
/// of a row — see <see cref="ClearRecipeRating"/> for the removal path.
/// </summary>
public sealed class RateRecipe(
    IRecipeRatingRepository ratings,
    IRecipeRepository recipes,
    ITenantContext tenant,
    IClock clock,
    ILogger<RateRecipe> logger)
{
    public async Task<Result> ExecuteAsync(RateRecipeCommand command, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("Rate rejected — no authenticated household.");
            return Error.Unauthorized;
        }
        var household = HouseholdId.From(householdGuid);

        if (command.Stars is < 1 or > 5)
        {
            logger.LogWarning(
                "Rate rejected for recipe {RecipeId} — invalid stars {Stars}.",
                command.RecipeId.Value, command.Stars);
            return Error.Custom("Recipes.InvalidStars", "Stars must be between 1 and 5.");
        }

        var recipe = await recipes.GetByIdAsync(command.RecipeId, ct);
        if (recipe is null)
        {
            logger.LogWarning("Rate failed — recipe {RecipeId} not found.", command.RecipeId.Value);
            return Error.NotFound;
        }

        var existing = await ratings.FindAsync(command.RecipeId, command.UserId, ct);
        if (existing is null)
        {
            var rating = RecipeRating.Create(household, command.RecipeId, command.UserId, command.Stars, clock);
            await ratings.AddAsync(rating, ct);
        }
        else
        {
            existing.SetStars(command.Stars, clock);
        }

        await ratings.SaveChangesAsync(ct);
        logger.LogInformation(
            "Recipe {RecipeId} rated {Stars} stars by user {UserId}.",
            command.RecipeId.Value, command.Stars, command.UserId);
        return Result.Success();
    }
}

/// <summary>
/// Input for <see cref="RateRecipe"/>. <see cref="UserId"/> is the identity of the rating member —
/// captured from the request principal at the Web layer and passed in explicitly, mirroring
/// <c>CookRecipeCommand.UserId</c> (not read from <c>ITenantContext</c>, which only carries household
/// identity).
/// </summary>
public sealed record RateRecipeCommand(RecipeId RecipeId, Guid UserId, int Stars);
