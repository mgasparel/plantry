using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D2 (tidy-up.md §3): flags a tracked recipe ingredient line whose authored unit has no conversion
/// path to its product's default (stock) unit — including gaps ADR-022's AI seeding never resolved
/// (a seeding attempt that fails, or one that was never triggered because no AI inferrer is
/// configured). Reuses exactly the check <c>ConversionGapPlanner</c> runs at author time (R7/C10):
/// a tracked line whose unit differs from the product default and whose conversion round-trip fails.
/// Tidy Up is the after-the-fact backstop for gaps that slipped past (or were deferred at) authoring
/// time.
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IRecipeFactsReadModel"/> (shared with D5/D7)
/// and runs the conversion check through the shared <c>Plantry.Catalog.Domain.UnitConverter</c> delegate
/// (see <c>HousekeepingConversions.BuildConverter</c>) rather than the retired
/// <c>IRecipeRepository</c>/<c>ICatalogProductReader</c>/<c>IUnitConverter</c> ports — the math below is
/// unchanged from the original port-backed version.
/// </para>
/// </summary>
public sealed class RecipeConversionGapDetector(
    IRecipeFactsReadModel factsReadModel,
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

        var bag = await factsReadModel.LoadAsync(ct);
        if (bag.Recipes.Count == 0)
            return [];

        var converter = bag.BuildConverter();

        var findings = new List<Finding>();
        foreach (var recipe in bag.Recipes.Values)
        {
            foreach (var ingredient in bag.GetIngredients(recipe.RecipeId))
            {
                if (ingredient.UnitId is not { } unitId)
                    continue; // untracked staple ("to taste") — R5, no quantity/unit to convert
                if (!bag.Products.TryGetValue(ingredient.ProductId, out var product))
                    continue; // product archived/removed from catalog — skip
                if (!product.TrackStock)
                    continue; // untracked product — cooking never deducts it, no conversion needed (R7)
                if (unitId == product.DefaultUnitId)
                    continue; // already the product's own unit — nothing to convert

                if (converter(product.ProductId, 1m, unitId, product.DefaultUnitId).IsSuccess)
                    continue;

                // plantry-c7mg: anchor to the specific offending line so the editor opens scrolled and
                // highlighted on it (Edit.cshtml keys the #ingredient-{ordinal} anchor on the same
                // Ordinal). Accepted limitation: the anchor keys on ordinal, not the ingredient's domain
                // id, so if the recipe is edited between detection and clicking this link the highlight
                // may land on a neighbouring line. Cosmetic, self-corrects on the next detector run.
                findings.Add(new Finding(
                    Id,
                    SubjectId: ingredient.IngredientId,
                    SubjectName: product.Name,
                    Specifics: $"{recipe.Name} has no conversion for this line's unit",
                    Consequence: "Cooking can't deduct it from stock · recipe cost is incomplete",
                    FixUrl: $"/Recipes/{recipe.RecipeId}/Edit#ingredient-{ingredient.Ordinal}",
                    FixLabel: "Fix in recipe",
                    FactsFingerprint: Fingerprint(unitId, product.DefaultUnitId)));
            }
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
