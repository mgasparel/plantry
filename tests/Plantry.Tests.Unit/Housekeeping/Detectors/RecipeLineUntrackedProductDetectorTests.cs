using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="RecipeLineUntrackedProductDetector"/> (D7, tidy-up.md §3, redefined) over
/// an in-memory <see cref="RecipeFactsBag"/> — restores the fast coverage the retired fake-port test file
/// provided, including the null-quantity/unit ("to taste") staple line still firing and the
/// fingerprint-changes-on-re-pointed-product direction the L3 tests don't independently exercise.
/// </summary>
public sealed class RecipeLineUntrackedProductDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid VanillaId = Guid.NewGuid();
    private static readonly Guid FlourId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();

    private static UnitFact Gram => new(GramId, "g", "grams", "mass", 1m, true);

    private static RecipeFactsBag BagFor(Guid recipeId, string recipeName, RecipeIngredientFact ingredient, ProductFact product) =>
        new(
            new Dictionary<Guid, RecipeFact> { [recipeId] = new(recipeId, recipeName) },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            new Dictionary<Guid, ProductFact> { [product.ProductId] = product },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            new HashSet<Guid>());

    private static RecipeLineUntrackedProductDetector BuildDetector(RecipeFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeRecipeFactsReadModel(bag), tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Untracked product line, with quantity/unit — produces a finding")]
    public async Task UntrackedProductWithUnit_ProducesFinding()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, VanillaId, 1m, GramId, 0);
        var bag = BagFor(recipeId, "Sunday Pancakes", ingredient, new ProductFact(VanillaId, "Vanilla Extract", false, GramId));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.RecipeLineUntrackedProduct, finding.DetectorId);
        Assert.Equal(ingredient.IngredientId, finding.SubjectId);
        Assert.Equal("Vanilla Extract", finding.SubjectName);
        Assert.Contains("Sunday Pancakes", finding.Specifics);
        Assert.Equal($"/Recipes/{recipeId}/Edit#ingredient-0", finding.FixUrl);
    }

    [Fact(DisplayName = "Untracked staple line with no quantity/unit — still produces a finding")]
    public async Task UntrackedStapleLine_NoUnit_StillProducesFinding()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, VanillaId, null, null, 0);
        var bag = BagFor(recipeId, "Sunday Pancakes", ingredient, new ProductFact(VanillaId, "Vanilla Extract", false, GramId));

        Assert.Single(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Tracked product — never flagged")]
    public async Task TrackedProduct_NoFinding()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, FlourId, 200m, GramId, 0);
        var bag = BagFor(recipeId, "Sunday Pancakes", ingredient, new ProductFact(FlourId, "All-Purpose Flour", true, GramId));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, VanillaId, 1m, GramId, 0);
        var bag = BagFor(recipeId, "Sunday Pancakes", ingredient, new ProductFact(VanillaId, "Vanilla Extract", false, GramId));

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: the same product on a different recipe produces the same fingerprint")]
    public async Task Fingerprint_SameProduct_SameFingerprint_AcrossRecipes()
    {
        var recipeIdA = Guid.NewGuid();
        var ingredientA = new RecipeIngredientFact(Guid.NewGuid(), recipeIdA, VanillaId, 1m, GramId, 0);
        var findingA = Assert.Single(await BuildDetector(
            BagFor(recipeIdA, "Sunday Pancakes", ingredientA, new ProductFact(VanillaId, "Vanilla Extract", false, GramId))).DetectAsync());

        var recipeIdB = Guid.NewGuid();
        var ingredientB = new RecipeIngredientFact(Guid.NewGuid(), recipeIdB, VanillaId, 2m, GramId, 0);
        var findingB = Assert.Single(await BuildDetector(
            BagFor(recipeIdB, "Banana Bread", ingredientB, new ProductFact(VanillaId, "Vanilla Extract", false, GramId))).DetectAsync());

        Assert.Equal(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: re-pointing the line at a different untracked product changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentProduct()
    {
        var sugarId = Guid.NewGuid();

        var recipeVanillaId = Guid.NewGuid();
        var ingredientVanilla = new RecipeIngredientFact(Guid.NewGuid(), recipeVanillaId, VanillaId, 1m, GramId, 0);
        var findingVanilla = Assert.Single(await BuildDetector(
            BagFor(recipeVanillaId, "Sunday Pancakes", ingredientVanilla, new ProductFact(VanillaId, "Vanilla Extract", false, GramId))).DetectAsync());

        var recipeSugarId = Guid.NewGuid();
        var ingredientSugar = new RecipeIngredientFact(Guid.NewGuid(), recipeSugarId, sugarId, 1m, GramId, 0);
        var findingSugar = Assert.Single(await BuildDetector(
            BagFor(recipeSugarId, "Sunday Pancakes", ingredientSugar, new ProductFact(sugarId, "Powdered Sugar", false, GramId))).DetectAsync());

        Assert.NotEqual(findingVanilla.FactsFingerprint, findingSugar.FactsFingerprint);
    }
}
