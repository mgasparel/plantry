using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service that clears a household member's star rating on a recipe (plantry-zlwp.1) — no
/// opinion is the ABSENCE of a row, so this deletes the <see cref="RecipeRating"/> row rather than
/// nulling a column, the same convention as <c>UserPreference</c>'s Neutral stance. No-op (success) when
/// no rating exists — clearing an already-absent rating is not an error.
/// </summary>
public sealed class ClearRecipeRating(
    IRecipeRatingRepository ratings,
    ILogger<ClearRecipeRating> logger)
{
    public async Task<Result> ExecuteAsync(ClearRecipeRatingCommand command, CancellationToken ct = default)
    {
        var existing = await ratings.FindAsync(command.RecipeId, command.UserId, ct);
        if (existing is null)
            return Result.Success();

        ratings.Remove(existing);
        await ratings.SaveChangesAsync(ct);
        logger.LogInformation(
            "Rating cleared for recipe {RecipeId} by user {UserId}.", command.RecipeId.Value, command.UserId);
        return Result.Success();
    }
}

/// <summary>
/// Input for <see cref="ClearRecipeRating"/>. <see cref="UserId"/> is the identity of the member
/// clearing their own rating — captured from the request principal at the Web layer, mirroring
/// <see cref="RateRecipeCommand.UserId"/>.
/// </summary>
public sealed record ClearRecipeRatingCommand(RecipeId RecipeId, Guid UserId);
