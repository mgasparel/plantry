using System.Security.Cryptography;
using System.Text;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D2 (tidy-up.md §3): flags a tracked recipe ingredient line whose authored unit has no conversion
/// path to its product's default (stock) unit — including gaps ADR-022's AI seeding never resolved
/// (a seeding attempt that fails, or one that was never triggered because no AI inferrer is
/// configured). Reuses exactly the check <c>ConversionGapPlanner</c> runs at author time (R7/C10):
/// a tracked line whose unit differs from the product default and whose <see cref="IUnitConverter"/>
/// round-trip fails. Tidy Up is the after-the-fact backstop for gaps that slipped past (or were
/// deferred at) authoring time.
/// </summary>
public sealed class RecipeConversionGapDetector(
    IRecipeRepository recipes,
    ICatalogProductReader products,
    IUnitConverter unitConverter,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.RecipeConversionGap;
    public Severity Severity => Severity.BehaviorAffecting;
    public string GroupTitle => "Recipe lines without a conversion path";
    public string GroupConsequence =>
        "A recipe line's unit has no path to the product's stock unit — cooking can't deduct it and its recipe cost is incomplete.";
    public string IconName => "i-scale";

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

        // First pass: gather every line that needs a conversion-path check, plus the distinct
        // (product, from-unit, to-unit) triples it implies — no converter calls yet. This lets the
        // second pass resolve all of them in one batched round trip instead of one per line
        // (plantry-4t0g — D1's ForProductsAsync pattern, mirrored here for IUnitConverter).
        var candidates = new List<(Ingredient Ingredient, Recipe Recipe, CatalogProductLookup Product)>();
        var triples = new HashSet<(Guid ProductId, Guid FromUnitId, Guid ToUnitId)>();
        foreach (var recipe in allRecipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                if (ingredient.UnitId is not { } unitId)
                    continue; // untracked staple ("to taste") — R5, no quantity/unit to convert
                if (!productsById.TryGetValue(ingredient.ProductId, out var product))
                    continue; // product archived/removed from catalog — skip
                if (!product.TrackStock)
                    continue; // untracked product — cooking never deducts it, no conversion needed (R7)
                if (unitId == product.DefaultUnitId)
                    continue; // already the product's own unit — nothing to convert

                candidates.Add((ingredient, recipe, product));
                triples.Add((product.Id, unitId, product.DefaultUnitId));
            }
        }

        if (candidates.Count == 0)
            return [];

        var unconvertible = await unitConverter.FindUnconvertiblePathsAsync(triples, ct);

        var findings = new List<Finding>();
        foreach (var (ingredient, recipe, product) in candidates)
        {
            var unitId = ingredient.UnitId!.Value;
            if (!unconvertible.Contains((product.Id, unitId, product.DefaultUnitId)))
                continue;

            // plantry-c7mg: anchor to the specific offending line so the editor opens scrolled and
            // highlighted on it (Edit.cshtml keys the #ingredient-{ordinal} anchor on the same
            // Ordinal). Accepted limitation: the anchor keys on ordinal, not the ingredient's domain
            // id, so if the recipe is edited between detection and clicking this link the highlight
            // may land on a neighbouring line. Cosmetic, self-corrects on the next detector run —
            // keying on the domain id was rejected as disproportionate plumbing (the GUID is not in
            // the editor row model; threading it would touch the input model, seeding, and POST
            // round-trip).
            findings.Add(new Finding(
                Id,
                SubjectId: ingredient.Id.Value,
                SubjectName: product.Name,
                Specifics: $"{recipe.Name} has no conversion for this line's unit",
                Consequence: "Cooking can't deduct it from stock · recipe cost is incomplete",
                FixUrl: $"/Recipes/{recipe.Id.Value}/Edit#ingredient-{ingredient.Ordinal}",
                FixLabel: "Fix in recipe",
                FactsFingerprint: Fingerprint(unitId, product.DefaultUnitId)));
        }

        return findings;
    }

    /// <summary>The authored unit + the product's default unit — not the quantity (§4). Either axis
    /// changing is a genuinely different gap; more/less of the same unit is not.</summary>
    private static string Fingerprint(Guid lineUnitId, Guid defaultUnitId)
    {
        var raw = $"{lineUnitId}|{defaultUnitId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
