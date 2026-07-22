using System.Security.Cryptography;
using System.Text;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D7 (tidy-up.md §3), REDEFINED 2026-07-22 (agreed with the product owner): the doc's original D7 —
/// "a free-text ingredient line unresolved to any product" — has no representation in the current domain
/// model. <see cref="Plantry.Recipes.Domain.Ingredient.ProductId"/> is a non-nullable <see cref="Guid"/>
/// (R4/C12); every persisted ingredient line, whether typed against an existing product or inline-created
/// during authoring, always resolves to one — there is no unlinked state to detect. D7 is redefined to
/// flag the closest real gap instead: an ingredient line whose product resolves with
/// <c>TrackStock == false</c> — exactly the lines <see cref="RecipeConversionGapDetector"/> (D2) already
/// skips at its own TrackStock guard, since cooking never deducts them and costing can never complete for
/// them. Often intentional (water, spices) — dismissal is the designed answer; this bead deliberately adds
/// no suppression heuristics for "obviously fine to leave untracked."
/// <para>
/// Fingerprint is the line's <see cref="Plantry.Recipes.Domain.Ingredient.ProductId"/> alone: re-pointing
/// the same line at a different untracked product is a new gap and must reopen a dismissed finding; the
/// product later becoming tracked simply stops the finding from firing (the detector's own condition
/// handles that, not the fingerprint).
/// </para>
/// </summary>
public sealed class RecipeLineUntrackedProductDetector(
    IRecipeRepository recipes,
    ICatalogProductReader products,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.RecipeLineUntrackedProduct;
    public Severity Severity => Severity.Advisory;
    public string GroupTitle => "Recipe lines using an untracked product";
    public string GroupConsequence =>
        "A recipe line's product isn't stock-tracked — cooking won't deduct it, it never joins the shopping list, and the line's cost is incomplete. Often intentional (water, spices).";
    public string IconName => "i-recipe";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var allRecipes = await recipes.ListForBrowseAsync(ct);
        if (allRecipes.Count == 0)
            return [];

        var productIds = allRecipes
            .SelectMany(r => r.Ingredients)
            .Select(i => i.ProductId)
            .Distinct()
            .ToList();
        if (productIds.Count == 0)
            return [];

        var productsById = await products.FindManyAsync(productIds, ct);

        var findings = new List<Finding>();
        foreach (var recipe in allRecipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                if (!productsById.TryGetValue(ingredient.ProductId, out var product))
                    continue; // product archived/removed from catalog — skip, same as D2
                if (product.TrackStock)
                    continue;

                findings.Add(new Finding(
                    Id,
                    SubjectId: ingredient.Id.Value,
                    SubjectName: product.Name,
                    Specifics: $"{recipe.Name} uses this as an untracked product",
                    Consequence: "Cooking never deducts it · no shopping integration · line costing incomplete",
                    FixUrl: $"/Recipes/{recipe.Id.Value}/Edit#ingredient-{ingredient.Ordinal}",
                    FixLabel: "Fix in recipe",
                    FactsFingerprint: Fingerprint(ingredient.ProductId)));
            }
        }

        return findings;
    }

    /// <summary>The line's product id alone (§4): re-pointing the line at a different untracked product is
    /// a new gap and must reopen a dismissed finding.</summary>
    private static string Fingerprint(Guid productId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(productId.ToString())));
}
