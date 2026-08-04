using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="RecipeConversionGapDetector"/> (D2, tidy-up.md §3) over an in-memory
/// <see cref="RecipeFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the conversion-path-exists suppression guard and the untracked-staple short-circuit that
/// the L3 tests in <c>RecipeDetectorsTests.cs</c> don't independently exercise at this granularity.
/// </summary>
public sealed class RecipeConversionGapDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid FlourId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();
    private static readonly Guid EachId = Guid.NewGuid();

    private static UnitFact Gram => new(GramId, "g", "grams", "mass", 1m, true);
    private static UnitFact Each => new(EachId, "ea", "each", "count", null, false);
    private static ProductFact TrackedFlour => new(FlourId, "All-Purpose Flour", true, GramId);

    private static RecipeFactsBag BagFor(
        Guid recipeId,
        string recipeName,
        IReadOnlyList<RecipeIngredientFact> ingredients,
        ProductFact product,
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>>? conversionsByProduct = null) =>
        new(
            new Dictionary<Guid, RecipeFact> { [recipeId] = new(recipeId, recipeName) },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = ingredients },
            new Dictionary<Guid, ProductFact> { [product.ProductId] = product },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram, [EachId] = Each },
            conversionsByProduct ?? new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new HashSet<Guid>());

    private static RecipeConversionGapDetector BuildDetector(RecipeFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeRecipeFactsReadModel(bag), tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Tracked line, unit differs from default, conversion path exists — no finding")]
    public async Task ConversionPathExists_NoFinding()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 2m, EachId, 0);
        var conversions = new Dictionary<Guid, IReadOnlyList<ConversionFact>>
        {
            [FlourId] = [new ConversionFact(FlourId, EachId, GramId, 125m)],
        };
        var bag = BagFor(recipeId, "Bread", [ingredient], TrackedFlour, conversions);

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Untracked staple line (no quantity/unit) — no finding")]
    public async Task UntrackedStapleLine_NoUnit_NoFinding()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, null, null, 0);
        var bag = BagFor(recipeId, "Bread", [ingredient], TrackedFlour);

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Tracked line, unit differs from default, no conversion path — produces a finding, FixUrl anchors on the offending line's own ordinal (plantry-c7mg regression lock)")]
    public async Task NoConversionPath_ProducesFinding()
    {
        var recipeId = Guid.NewGuid();
        // plantry-c7mg regression lock: a harmless line at ordinal 0 (already the default unit, no
        // finding) sits ahead of the offending line at ordinal 1 — proves the anchor tracks the
        // specific flagged line rather than a fixed/first-line value.
        var harmlessLine = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 200m, GramId, 0);
        var offendingLine = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 2m, EachId, 1);
        var bag = BagFor(recipeId, "Bread", [harmlessLine, offendingLine], TrackedFlour);

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.RecipeConversionGap, finding.DetectorId);
        Assert.Equal(offendingLine.IngredientId, finding.SubjectId);
        Assert.Equal("All-Purpose Flour", finding.SubjectName);
        Assert.Contains("Bread", finding.Specifics);
        Assert.Equal($"/Recipes/{recipeId}/Edit#ingredient-1", finding.FixUrl);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 2m, EachId, 0);
        var bag = BagFor(recipeId, "Bread", [ingredient], TrackedFlour);

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: same (line unit, default unit) pair on different recipes/lines — equal fingerprint")]
    public async Task Fingerprint_SamePair_Equal()
    {
        var recipeIdA = Guid.NewGuid();
        var ingredientA = new RecipeIngredientFact(Guid.NewGuid(), recipeIdA, FlourId, 2m, EachId, 0);
        var findingA = Assert.Single(
            await BuildDetector(BagFor(recipeIdA, "Bread", [ingredientA], TrackedFlour)).DetectAsync());

        var recipeIdB = Guid.NewGuid();
        var ingredientB = new RecipeIngredientFact(Guid.NewGuid(), recipeIdB, FlourId, 5m, EachId, 0);
        var findingB = Assert.Single(
            await BuildDetector(BagFor(recipeIdB, "Cake", [ingredientB], TrackedFlour)).DetectAsync());

        Assert.Equal(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a different default unit changes the fingerprint")]
    public async Task Fingerprint_DifferentDefaultUnit_Changes()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 2m, EachId, 0);
        var findingGramDefault = Assert.Single(
            await BuildDetector(BagFor(recipeId, "Bread", [ingredient], TrackedFlour)).DetectAsync());

        var otherUnitId = Guid.NewGuid();
        var otherUnit = new UnitFact(otherUnitId, "ml", "milliliters", "volume", 1m, true);
        var productDifferentDefault = TrackedFlour with { DefaultUnitId = otherUnitId };
        var bagDifferentDefault = new RecipeFactsBag(
            new Dictionary<Guid, RecipeFact> { [recipeId] = new(recipeId, "Bread") },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            new Dictionary<Guid, ProductFact> { [FlourId] = productDifferentDefault },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram, [EachId] = Each, [otherUnitId] = otherUnit },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new HashSet<Guid>());
        var findingOtherDefault = Assert.Single(await BuildDetector(bagDifferentDefault).DetectAsync());

        Assert.NotEqual(findingGramDefault.FactsFingerprint, findingOtherDefault.FactsFingerprint);
    }
}
