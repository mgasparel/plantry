using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D5 (tidy-up.md §3): flags a tracked product (<c>TrackStock == true</c>) referenced by at least one
/// recipe ingredient line that has zero recorded price observations. Untracked products are excluded —
/// D7 owns "line uses an untracked product"; flagging "water has no price" on both would be noise.
/// <para>
/// Fingerprint is constant per subject (§4): the gap is binary (a price observation exists or it
/// doesn't), so dismissal is permanent.
/// </para>
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IRecipeFactsReadModel"/> (shared with D2/D7),
/// whose <c>PricedProductIds</c> query mirrors the retired
/// <c>PricingQueries.ProductIdsWithAnyPriceAsync</c> batch existence check exactly — rather than the
/// retired <c>IRecipeRepository</c>/<c>ICatalogProductReader</c>/<c>PricingQueries</c> ports.
/// </para>
/// </summary>
public sealed class RecipeIngredientNoPriceDetector(
    IRecipeFactsReadModel factsReadModel,
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

        var bag = await factsReadModel.LoadAsync(ct);
        if (bag.Recipes.Count == 0)
            return [];

        // Product -> the recipe names that reference it (for Specifics), in first-seen order.
        var recipeNamesByProduct = new Dictionary<Guid, List<string>>();
        foreach (var recipe in bag.Recipes.Values)
        {
            foreach (var ingredient in bag.GetIngredients(recipe.RecipeId))
            {
                if (!recipeNamesByProduct.TryGetValue(ingredient.ProductId, out var names))
                    recipeNamesByProduct[ingredient.ProductId] = names = [];
                if (!names.Contains(recipe.Name))
                    names.Add(recipe.Name);
            }
        }
        if (recipeNamesByProduct.Count == 0)
            return [];

        var findings = new List<Finding>();
        foreach (var (productId, recipeNames) in recipeNamesByProduct)
        {
            if (!bag.Products.TryGetValue(productId, out var product))
                continue; // product archived/removed from catalog — skip, same as D2/D7
            if (!product.TrackStock)
                continue;
            if (bag.PricedProductIds.Contains(productId))
                continue;

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
