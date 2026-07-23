using System.Security.Cryptography;
using System.Text;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.Pricing.Application;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D5 (tidy-up.md §3): flags a tracked product (<c>TrackStock == true</c>) referenced by at least one
/// recipe ingredient line that has zero recorded price observations. Untracked products are excluded —
/// D7 owns "line uses an untracked product"; flagging "water has no price" on both would be noise.
/// Batches the price-existence check via <see cref="PricingQueries.ProductIdsWithAnyPriceAsync"/> (the
/// plantry-4t0g convention D1/D2 already established) rather than a per-product round trip.
/// <para>
/// Fingerprint is constant per subject (§4): the gap is binary (a price observation exists or it
/// doesn't), so dismissal is permanent.
/// </para>
/// </summary>
public sealed class RecipeIngredientNoPriceDetector(
    IRecipeRepository recipes,
    ICatalogProductReader products,
    PricingQueries pricing,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.RecipeIngredientNoPriceData;
    public Severity Severity => Severity.Advisory;
    public string GroupTitle => "Recipe ingredients with no price data";
    public string GroupConsequence =>
        "A product used in a recipe has never had a price recorded — that recipe's cost-per-serving is silently incomplete.";
    public string IconName => "i-coins";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var allRecipes = await recipes.ListForBrowseAsync(ct);
        if (allRecipes.Count == 0)
            return [];

        // Product -> the recipe names that reference it (for Specifics), in first-seen order.
        var recipeNamesByProduct = new Dictionary<Guid, List<string>>();
        foreach (var recipe in allRecipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                if (!recipeNamesByProduct.TryGetValue(ingredient.ProductId, out var names))
                    recipeNamesByProduct[ingredient.ProductId] = names = [];
                if (!names.Contains(recipe.Name))
                    names.Add(recipe.Name);
            }
        }
        if (recipeNamesByProduct.Count == 0)
            return [];

        var productIds = recipeNamesByProduct.Keys.ToList();
        var productsById = await products.FindManyAsync(productIds, ct);
        var trackedProductIds = productsById.Values.Where(p => p.TrackStock).Select(p => p.Id).ToList();
        if (trackedProductIds.Count == 0)
            return [];

        var pricedProductIds = await pricing.ProductIdsWithAnyPriceAsync(trackedProductIds, ct);

        var findings = new List<Finding>();
        foreach (var productId in trackedProductIds)
        {
            if (pricedProductIds.Contains(productId))
                continue;

            var product = productsById[productId];
            var recipeNames = recipeNamesByProduct[productId];
            var specifics = recipeNames.Count == 1
                ? $"No price recorded — used in {recipeNames[0]}"
                : $"No price recorded — used in {recipeNames.Count} recipes";

            findings.Add(new Finding(
                Id,
                SubjectId: productId,
                SubjectName: product.Name,
                Specifics: specifics,
                Consequence: "Recipe cost-per-serving is silently incomplete",
                FixUrl: $"/Pantry/Products/Detail/{productId}",
                FixLabel: "Set price in Pantry",
                FactsFingerprint: ConstantFingerprint));
        }

        return findings;
    }

    /// <summary>Constant per subject (§4): the gap is binary — a price observation exists or it doesn't —
    /// so dismissal is permanent.</summary>
    private static readonly string ConstantFingerprint =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("d5-recipe-ingredient-no-price-data")));
}
