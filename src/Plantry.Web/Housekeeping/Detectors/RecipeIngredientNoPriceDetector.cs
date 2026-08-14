using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel;
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
/// rather than the retired <c>IRecipeRepository</c>/<c>ICatalogProductReader</c>/<c>PricingQueries</c> ports.
/// "Has a price" is a live-variant rollup, not raw existence (plantry-i07l rule 5): a concrete product is
/// priced by its own usable observation; a parent is priced only when at least one live (non-archived)
/// variant has a usable, convertible, non-superseded candidate. Parent-only, archived-only, and
/// unusable/unconvertible observations never clear the finding — so D5 agrees with recipe costing.
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

        var converter = bag.BuildConverter();
        var findings = new List<Finding>();
        foreach (var (productId, recipeNames) in recipeNamesByProduct)
        {
            if (!bag.Products.TryGetValue(productId, out var product))
                continue; // product archived/removed from catalog — skip, same as D2/D7
            if (!product.TrackStock)
                continue;
            if (HasUsableCandidate(bag, product, converter))
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

    /// <summary>
    /// True when the referenced product has a price under the shared live-variant rollup (plantry-i07l),
    /// so D5 agrees with recipe costing:
    /// <list type="bullet">
    /// <item>A <b>concrete product</b> is priced iff it has any usable live observation on itself (a leaf
    /// keeps its observation in its own unit — no conversion, mirroring <c>EffectivePriceRollup</c>).</item>
    /// <item>A <b>parent</b> is priced iff at least one <b>live (non-archived) variant</b> has a usable
    /// observation that converts to the parent's default unit. Parent-only observations, archived-only
    /// variants, and unusable/unconvertible observations never count (rules 2/3/5).</item>
    /// </list>
    /// </summary>
    private static bool HasUsableCandidate(
        RecipeFactsBag bag, ProductFact product, Func<Guid, decimal, Guid, Guid, Result<decimal>> convert)
    {
        // Concrete product: resolves to itself — priced iff it has a usable observation (identity, no
        // conversion). Matches EffectivePriceRollup's leaf self-resolution.
        if (!product.IsParent)
            return bag.PriceObservations.TryGetValue(product.ProductId, out var leafObservations)
                && leafObservations.Any(IsUsable);

        // Parent: roll up live direct variants only.
        if (!bag.LiveVariantsByParent.TryGetValue(product.ProductId, out var variants))
            return false;

        foreach (var variant in variants)
        {
            if (!bag.PriceObservations.TryGetValue(variant.VariantId, out var observations))
                continue;

            foreach (var observation in observations)
            {
                if (!IsUsable(observation))
                    continue;

                // Reference unit for comparison (rule 3): the parent's default unit when known; otherwise
                // the observation's own unit (identity — no conversion), mirroring the rollup.
                var referenceUnit = product.DefaultUnitId != Guid.Empty ? product.DefaultUnitId : observation.UnitId;
                if (referenceUnit == observation.UnitId)
                    return true;

                var converted = convert(variant.VariantId, 1m, observation.UnitId, referenceUnit);
                if (converted.IsSuccess && converted.Value > 0m)
                    return true;
            }
        }
        return false;
    }

    /// <summary>A usable observation has a positive quantity and a real (non-empty) unit — the rollup's
    /// gate; an empty-quantity or unitless (DM-17) row has no conversion basis and cannot price anything.</summary>
    private static bool IsUsable(PriceObservationFact observation) =>
        observation.Quantity > 0m && observation.UnitId != Guid.Empty;

    /// <summary>Constant per subject (§4): the gap is binary — a price observation exists or it doesn't —
    /// so dismissal is permanent.</summary>
    private static readonly string ConstantFingerprint =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("d5-recipe-ingredient-no-price-data")));
}
