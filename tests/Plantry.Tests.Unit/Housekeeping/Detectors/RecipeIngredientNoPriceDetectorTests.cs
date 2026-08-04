using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="RecipeIngredientNoPriceDetector"/> (D5, tidy-up.md §3) over an in-memory
/// <see cref="RecipeFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the multi-recipe pluralization branch in <c>Specifics</c> that the L3 tests don't
/// independently exercise.
/// </summary>
public sealed class RecipeIngredientNoPriceDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid FlourId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();

    private static UnitFact Gram => new(GramId, "g", "grams", "mass", 1m, true);
    private static ProductFact TrackedFlour => new(FlourId, "All-Purpose Flour", true, GramId);

    private static (Guid RecipeId, RecipeFact Recipe, RecipeIngredientFact Ingredient) MakeRecipe(string name, Guid productId) =>
        MakeRecipe(name, productId, Guid.NewGuid());

    private static (Guid RecipeId, RecipeFact Recipe, RecipeIngredientFact Ingredient) MakeRecipe(
        string name, Guid productId, Guid recipeId)
    {
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, productId, 200m, GramId, 0);
        return (recipeId, new RecipeFact(recipeId, name), ingredient);
    }

    private static RecipeFactsBag BagFor(
        IReadOnlyDictionary<Guid, RecipeFact> recipes,
        IReadOnlyDictionary<Guid, IReadOnlyList<RecipeIngredientFact>> ingredientsByRecipe,
        ProductFact product,
        IReadOnlySet<Guid>? pricedProductIds = null) =>
        new(
            recipes,
            ingredientsByRecipe,
            new Dictionary<Guid, ProductFact> { [product.ProductId] = product },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            pricedProductIds ?? new HashSet<Guid>());

    private static RecipeIngredientNoPriceDetector BuildDetector(RecipeFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeRecipeFactsReadModel(bag), tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Tracked product with zero price observations — produces a finding")]
    public async Task TrackedProductNoPrice_ProducesFinding()
    {
        var (recipeId, recipe, ingredient) = MakeRecipe("Sunday Pancakes", FlourId);
        var bag = BagFor(
            new Dictionary<Guid, RecipeFact> { [recipeId] = recipe },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            TrackedFlour);

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.RecipeIngredientNoPriceData, finding.DetectorId);
        Assert.Equal(FlourId, finding.SubjectId);
        Assert.Equal("All-Purpose Flour", finding.SubjectName);
        Assert.Contains("Sunday Pancakes", finding.Specifics);
        Assert.Equal($"/Pantry/Products/Detail/{FlourId}", finding.FixUrl);
    }

    [Fact(DisplayName = "Tracked product WITH a price observation — no finding")]
    public async Task TrackedProductWithPrice_NoFinding()
    {
        var (recipeId, recipe, ingredient) = MakeRecipe("Sunday Pancakes", FlourId);
        var bag = BagFor(
            new Dictionary<Guid, RecipeFact> { [recipeId] = recipe },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            TrackedFlour,
            new HashSet<Guid> { FlourId });

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Untracked product — excluded even with zero price observations (D7's territory)")]
    public async Task UntrackedProduct_NoFinding()
    {
        var (recipeId, recipe, ingredient) = MakeRecipe("Sunday Pancakes", FlourId);
        var bag = BagFor(
            new Dictionary<Guid, RecipeFact> { [recipeId] = recipe },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            TrackedFlour with { TrackStock = false });

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Product used in two recipes — Specifics reports the recipe count")]
    public async Task UsedInMultipleRecipes_ReportsCount()
    {
        var (recipeIdA, recipeA, ingredientA) = MakeRecipe("Sunday Pancakes", FlourId);
        var (recipeIdB, recipeB, ingredientB) = MakeRecipe("Banana Bread", FlourId);
        var bag = BagFor(
            new Dictionary<Guid, RecipeFact> { [recipeIdA] = recipeA, [recipeIdB] = recipeB },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>>
            {
                [recipeIdA] = [ingredientA],
                [recipeIdB] = [ingredientB],
            },
            TrackedFlour);

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Contains("2 recipes", finding.Specifics);
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var (recipeId, recipe, ingredient) = MakeRecipe("Sunday Pancakes", FlourId);
        var bag = BagFor(
            new Dictionary<Guid, RecipeFact> { [recipeId] = recipe },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            TrackedFlour);

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }
}
