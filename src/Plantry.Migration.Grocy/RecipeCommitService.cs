using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Executes the Recipe commit step of the Grocy import pipeline (grocy-import-plan.md §5.1–§5.4, §7).
///
/// Algorithm:
/// 1. For each staged recipe, build an <see cref="AuthorRecipeCommand"/> from the staging row.
///    Commit order: Recipes → Recipe ingredients (embedded in AuthorRecipe) → Recipe photos.
/// 2. Commit via <see cref="AuthorRecipe"/>; RLS + household scoping is automatic.
/// 3. On <see cref="AuthorRecipeResult.Saved"/>: write <c>grocy_recipe_id → plantry_recipe_id</c>
///    to the running crosswalk.
/// 4. On <see cref="AuthorRecipeResult.Invalid"/> with DuplicateName: idempotent — recipe was
///    committed on a prior run. Look it up by name and record it in the crosswalk.
/// 5. Photo commit: call <see cref="Recipe.SetPhoto"/> on the loaded aggregate and save.
///    Photos are committed after the recipe body to keep the two saves independent and restartable.
/// 6. Ingredients with a missing crosswalk entry (PlantryProductId or PlantryUnitId null) are
///    skipped; the recipe is still committed without them and the row is flagged in the result.
/// 7. Idempotency: keyed on the grocy_recipe_id crosswalk. Re-running skips already-committed
///    recipes and only re-attempts photo commit if a photo was not previously attached.
/// </summary>
public sealed class RecipeCommitService(
    AuthorRecipe authorRecipe,
    IRecipeRepository recipes,
    IClock clock,
    ITenantContext tenant)
{
    // ──────────── Result types ──────────────────────────────────────────────

    public enum IngredientCommitDisposition
    {
        /// <summary>Committed as a recipe ingredient.</summary>
        Committed,

        /// <summary>Skipped because PlantryProductId or PlantryUnitId was null (crosswalk missing).</summary>
        SkippedCrosswalkMissing,
    }

    public sealed record IngredientCommitResult(
        int GrocyPositionId,
        IngredientCommitDisposition Disposition,
        string? Note);

    public enum PhotoCommitDisposition
    {
        /// <summary>Photo bytes were written to the recipe_photo column.</summary>
        Committed,

        /// <summary>No photo bytes were staged — nothing to commit.</summary>
        NoneStaged,

        /// <summary>Photo was already present on the recipe (re-run idempotency).</summary>
        AlreadyPresent,

        /// <summary>Photo commit encountered an error — recipe was still committed.</summary>
        Failed,
    }

    public sealed record RecipeCommitResult(
        int GrocyId,
        string GrocyName,
        bool Skipped,
        bool Success,
        Guid? PlantryRecipeId,
        string? ErrorMessage,
        IReadOnlyList<IngredientCommitResult> Ingredients,
        PhotoCommitDisposition PhotoDisposition,
        string? PhotoNote);

    // ──────────── Main commit method ────────────────────────────────────────

    /// <summary>
    /// Commits all staged recipes to the Recipes context, writes the crosswalk, and returns per-row results.
    ///
    /// Recipes with no committable ingredients (all crosswalk-missing) are skipped.
    /// Photos are committed independently after the recipe body — a photo failure does not roll back
    /// the recipe commit.
    /// </summary>
    /// <param name="stagingRows">Staging rows from <see cref="RecipeStager.Stage"/>.</param>
    /// <param name="manifestFilePath">Manifest file path — used to derive the crosswalk sidecar path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(IReadOnlyList<RecipeCommitResult> Results, string CrosswalkPath)> CommitAsync(
        IReadOnlyList<RecipeStagingRow> stagingRows,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            throw new InvalidOperationException("No household in tenant context — cannot commit recipes.");

        // ── Load existing crosswalk (idempotency: previously committed recipes) ─
        var crosswalkPath = RecipeCrosswalk.ResolvePath(manifestFilePath);
        var existingCrosswalk = await RecipeCrosswalk.TryReadAsync(crosswalkPath, ct);
        var crosswalkMappings = existingCrosswalk?.Mappings is not null
            ? new Dictionary<string, Guid>(existingCrosswalk.Mappings)
            : new Dictionary<string, Guid>();

        var results = new List<RecipeCommitResult>(stagingRows.Count);

        // Process each staged recipe in Grocy-id order (stable, predictable)
        foreach (var row in stagingRows.OrderBy(r => r.GrocyId))
        {
            var result = await CommitRecipeRowAsync(row, crosswalkMappings, ct);
            results.Add(result);
        }

        // ── Write updated crosswalk ─────────────────────────────────────────
        var crosswalk = new RecipeCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = crosswalkMappings,
        };
        await crosswalk.WriteAsync(crosswalkPath, ct);

        return (results, crosswalkPath);
    }

    // ──────────── Per-recipe commit ─────────────────────────────────────────

    private async Task<RecipeCommitResult> CommitRecipeRowAsync(
        RecipeStagingRow row,
        Dictionary<string, Guid> crosswalkMappings,
        CancellationToken ct)
    {
        try
        {
            // ── Build ingredient list (skip crosswalk-missing entries) ───────
            var (ingredientLines, ingredientResults, skippedCount) = BuildIngredientLines(row.Ingredients);

            // If every ingredient is crosswalk-missing we still commit the recipe body —
            // an empty ingredient list would fail R3, so skip the row entirely.
            if (ingredientLines.Count == 0)
            {
                return new RecipeCommitResult(
                    row.GrocyId, row.GrocyName,
                    Skipped: true, Success: true,
                    PlantryRecipeId: null,
                    ErrorMessage: "Skipped: all ingredients are crosswalk-missing; recipe cannot commit without at least one ingredient (R3).",
                    Ingredients: ingredientResults,
                    PhotoDisposition: PhotoCommitDisposition.NoneStaged,
                    PhotoNote: null);
            }

            // ── Idempotency: already in crosswalk? ───────────────────────────
            Guid plantryId;
            var alreadyInCrosswalk = crosswalkMappings.TryGetValue(row.GrocyId.ToString(), out var existingId);

            if (alreadyInCrosswalk)
            {
                plantryId = existingId;
                // Re-run: recipe body already committed — only need to check photo.
            }
            else
            {
                // ── Commit via AuthorRecipe ──────────────────────────────────
                var command = new AuthorRecipeCommand(
                    RecipeId:       null,   // create
                    Name:           row.PlantryName,
                    DefaultServings: Math.Max(1, row.BaseServings),
                    Lines:          ingredientLines,
                    TagNames:       [],     // Grocy recipes have no tags (plan §5.1)
                    Source:         row.Source,
                    CookTimeMinutes: null,  // Grocy has no cook-time field
                    Directions:     row.Directions,
                    ScaleMode:      ScaleMode.Keep);

                var authorResult = await authorRecipe.ExecuteAsync(command, ct);

                switch (authorResult)
                {
                    case AuthorRecipeResult.Saved saved:
                        plantryId = saved.RecipeId.Value;
                        crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                        break;

                    case AuthorRecipeResult.Invalid invalid
                        when invalid.Error.Code == "Recipes.DuplicateName":
                        // Idempotent: already committed on a prior run — find by name.
                        var foundByName = await FindRecipeByNameAsync(row.PlantryName, ct);
                        if (foundByName is null)
                        {
                            return new RecipeCommitResult(
                                row.GrocyId, row.GrocyName,
                                Skipped: false, Success: false,
                                PlantryRecipeId: null,
                                ErrorMessage: $"Recipe '{row.PlantryName}' reported as duplicate but could not be found.",
                                Ingredients: ingredientResults,
                                PhotoDisposition: PhotoCommitDisposition.Failed,
                                PhotoNote: null);
                        }
                        plantryId = foundByName.Value;
                        crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                        break;

                    case AuthorRecipeResult.Invalid invalid:
                        return new RecipeCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryRecipeId: null,
                            ErrorMessage: $"AuthorRecipe rejected: {invalid.Error.Description}",
                            Ingredients: ingredientResults,
                            PhotoDisposition: PhotoCommitDisposition.Failed,
                            PhotoNote: null);

                    case AuthorRecipeResult.NeedsConversion needs:
                        // One or more ingredient units have no conversion path to the product default.
                        // This means the product commit did not include those conversions. Return as a
                        // soft failure — the user can re-run after adding the conversions manually.
                        return new RecipeCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryRecipeId: null,
                            ErrorMessage: $"AuthorRecipe requires {needs.Conversions.Count} missing unit conversion(s). " +
                                          "Ensure all product conversions were committed before re-running recipe commit.",
                            Ingredients: ingredientResults,
                            PhotoDisposition: PhotoCommitDisposition.Failed,
                            PhotoNote: null);

                    default:
                        return new RecipeCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryRecipeId: null,
                            ErrorMessage: "Unexpected AuthorRecipe result type.",
                            Ingredients: ingredientResults,
                            PhotoDisposition: PhotoCommitDisposition.Failed,
                            PhotoNote: null);
                }
            }

            // ── Photo commit (independent of recipe body) ────────────────────
            var (photoDisposition, photoNote) = await CommitPhotoAsync(
                plantryId, row.PhotoBytes, row.PhotoContentType, ct);

            return new RecipeCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: false, Success: true,
                PlantryRecipeId: plantryId,
                ErrorMessage: null,
                Ingredients: ingredientResults,
                PhotoDisposition: photoDisposition,
                PhotoNote: photoNote);
        }
        catch (Exception ex)
        {
            return new RecipeCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: false, Success: false,
                PlantryRecipeId: null,
                ErrorMessage: $"Unexpected error: {ex.Message}",
                Ingredients: [],
                PhotoDisposition: PhotoCommitDisposition.Failed,
                PhotoNote: null);
        }
    }

    // ──────────── Photo commit ──────────────────────────────────────────────

    private async Task<(PhotoCommitDisposition Disposition, string? Note)> CommitPhotoAsync(
        Guid plantryId,
        byte[]? photoBytes,
        string? photoContentType,
        CancellationToken ct)
    {
        if (photoBytes is null || photoBytes.Length == 0)
            return (PhotoCommitDisposition.NoneStaged, null);

        try
        {
            // Load the recipe with its photo (GetByIdAsync includes photo)
            var recipe = await recipes.GetByIdAsync(Recipes.Domain.RecipeId.From(plantryId), ct);
            if (recipe is null)
                return (PhotoCommitDisposition.Failed, "Recipe not found after commit — cannot attach photo.");

            // Idempotency: if a photo already exists with the same length, treat as already committed.
            if (recipe.Photo is not null && recipe.Photo.Content.Length == photoBytes.Length)
                return (PhotoCommitDisposition.AlreadyPresent, null);

            // Compute SHA-256 for deduplication (nullable per RecipePhoto spec)
            byte[]? sha256 = null;
            try
            {
                sha256 = System.Security.Cryptography.SHA256.HashData(photoBytes);
            }
            catch
            {
                // SHA-256 is optional — proceed without it
            }

            recipe.SetPhoto(
                content: photoBytes,
                contentType: photoContentType ?? "image/jpeg",
                sha256: sha256,
                clock: clock);

            await recipes.SaveChangesAsync(ct);
            return (PhotoCommitDisposition.Committed, null);
        }
        catch (Exception ex)
        {
            return (PhotoCommitDisposition.Failed, $"Photo commit error: {ex.Message}");
        }
    }

    // ──────────── Ingredient line building ─────────────────────────────────

    private static (
        IReadOnlyList<AuthorIngredientLine> Lines,
        IReadOnlyList<IngredientCommitResult> Results,
        int SkippedCount
    ) BuildIngredientLines(IReadOnlyList<StagedIngredient> staged)
    {
        var lines   = new List<AuthorIngredientLine>(staged.Count);
        var results = new List<IngredientCommitResult>(staged.Count);
        var skipped = 0;

        // Re-index ordinals from 0 across only the committable ingredients
        var ordinal = 0;

        foreach (var ing in staged.OrderBy(i => i.Ordinal))
        {
            if (ing.PlantryProductId is null || ing.PlantryUnitId is null)
            {
                results.Add(new IngredientCommitResult(
                    ing.GrocyPositionId,
                    IngredientCommitDisposition.SkippedCrosswalkMissing,
                    $"Skipped: PlantryProductId={ing.PlantryProductId?.ToString() ?? "null"}, " +
                    $"PlantryUnitId={ing.PlantryUnitId?.ToString() ?? "null"} (crosswalk missing)."));
                skipped++;
                continue;
            }

            lines.Add(new AuthorIngredientLine(
                ProductId:           ing.PlantryProductId,
                Quantity:            ing.Amount,
                UnitId:              ing.PlantryUnitId,
                GroupHeading:        ing.GroupHeading,
                Ordinal:             ordinal++,
                NewStapleName:       null,
                NewStapleDefaultUnitId: null,
                ConversionFactor:    null));

            results.Add(new IngredientCommitResult(
                ing.GrocyPositionId,
                IngredientCommitDisposition.Committed,
                Note: null));
        }

        return (lines, results, skipped);
    }

    // ──────────── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Looks up a recipe by name using <see cref="IRecipeRepository.ListForBrowseAsync"/>.
    /// Returns the recipe GUID when found, null otherwise.
    /// </summary>
    private async Task<Guid?> FindRecipeByNameAsync(string name, CancellationToken ct)
    {
        var all = await recipes.ListForBrowseAsync(ct);
        var found = all.FirstOrDefault(r =>
            string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return found?.Id.Value;
    }
}
