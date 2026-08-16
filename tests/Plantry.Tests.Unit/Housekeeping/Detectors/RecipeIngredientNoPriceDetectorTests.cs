using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;
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
        IReadOnlySet<Guid>? pricedProductIds = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<LiveVariantFact>>? liveVariantsByParent = null)
    {
        // A priced id maps to a usable observation in Gram units (quantity 1, price 1) — sufficient for
        // D5's "has a usable candidate" test on a concrete leaf.
        var observations = (pricedProductIds ?? new HashSet<Guid>())
            .ToDictionary(id => id, id => (IReadOnlyList<PriceObservationFact>)
                new[] { new PriceObservationFact(id, 1m, 1m, GramId, 1m) });

        return new RecipeFactsBag(
            recipes,
            ingredientsByRecipe,
            new Dictionary<Guid, ProductFact> { [product.ProductId] = product },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            observations,
            liveVariantsByParent ?? new Dictionary<Guid, IReadOnlyList<LiveVariantFact>>());
    }

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

    // ── Parent / variant rollup (plantry-i07l rule 5) ─────────────────────────────────────────────

    private static readonly Guid ParentId = Guid.NewGuid();
    private static readonly Guid HikariId = Guid.NewGuid();

    /// <summary>Builds a bag whose single ingredient references the given parent product (IsParent) with the
    /// supplied live-variant map, price-observation facts, and variant conversions.</summary>
    private static RecipeFactsBag ParentBagFor(
        string recipeName,
        ProductFact parent,
        IReadOnlyList<LiveVariantFact> variants,
        IReadOnlyDictionary<Guid, IReadOnlyList<PriceObservationFact>> observations,
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>>? conversions = null)
    {
        var recipeId = Guid.NewGuid();
        var ingredient = new RecipeIngredientFact(Guid.NewGuid(), recipeId, parent.ProductId, 200m, GramId, 0);
        return new RecipeFactsBag(
            new Dictionary<Guid, RecipeFact> { [recipeId] = new(recipeId, recipeName) },
            new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>> { [recipeId] = [ingredient] },
            new Dictionary<Guid, ProductFact> { [parent.ProductId] = parent },
            new Dictionary<Guid, UnitFact> { [GramId] = Gram },
            conversions ?? new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
            observations,
            new Dictionary<Guid, IReadOnlyList<LiveVariantFact>> { [parent.ProductId] = variants });
    }

    [Fact(DisplayName = "Parent with one live, priced variant — clears the finding (usable live variant)")]
    public async Task Parent_WithPricedLiveVariant_Clears()
    {
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var liveVariant = new LiveVariantFact(HikariId, GramId);
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [HikariId] = [new PriceObservationFact(HikariId, 1.80m, 100m, GramId, 0.018m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [liveVariant], observations)).DetectAsync();

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "Parent with only a parent observation (no live variant priced) — still fires")]
    public async Task Parent_ParentOnlyObservation_StillFires()
    {
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var liveVariant = new LiveVariantFact(HikariId, GramId);
        // A usable observation on the PARENT itself (orphaned legacy row) must not count (rule 5).
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [ParentId] = [new PriceObservationFact(ParentId, 1.80m, 100m, GramId, 0.018m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [liveVariant], observations)).DetectAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(ParentId, finding.SubjectId);
    }

    [Fact(DisplayName = "Parent with zero live variants — still fires")]
    public async Task Parent_NoLiveVariants_StillFires()
    {
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [ParentId] = [new PriceObservationFact(ParentId, 1.80m, 100m, GramId, 0.018m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [], observations)).DetectAsync();

        Assert.Single(findings);
    }

    [Fact(DisplayName = "Parent with only archived variants (observations not counted) — still fires")]
    public async Task Parent_OnlyArchivedVariants_StillFires()
    {
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        // An archived variant is absent from LiveVariantsByParent (the read model loads live only), so
        // its observations must not clear even though they are present in the fact map.
        var archivedVariant = new LiveVariantFact(HikariId, GramId);
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [HikariId] = [new PriceObservationFact(HikariId, 1.80m, 100m, GramId, 0.018m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [], observations)).DetectAsync();

        // Note: the `archivedVariant` variable is intentionally NOT in the live list passed above.
        Assert.Single(findings);
    }

    [Fact(DisplayName = "Parent with a live variant but an unusable (empty-unit) observation — still fires")]
    public async Task Parent_LiveVariantUsableUnit_Only_StillFires_WhenUnitless()
    {
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var liveVariant = new LiveVariantFact(HikariId, GramId);
        // Unitless deal (DM-17 writes unit_id = Guid.Empty) — no conversion basis, must not count.
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [HikariId] = [new PriceObservationFact(HikariId, 1.80m, 100m, Guid.Empty, 0.018m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [liveVariant], observations)).DetectAsync();

        Assert.Single(findings);
    }

    [Fact(DisplayName = "Parent with a live variant whose observation cannot convert to the parent unit — still fires")]
    public async Task Parent_LiveVariantUnconvertible_StillFires()
    {
        var eachId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var liveVariant = new LiveVariantFact(HikariId, eachId);
        // Variant observed in 'each' (count dimension); parent default is 'gram' (mass). No conversion
        // path exists → the candidate is unusable/unconvertible and does not clear the finding (rule 3).
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [HikariId] = [new PriceObservationFact(HikariId, 1.80m, 1m, eachId, 1.80m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [liveVariant], observations)).DetectAsync();

        Assert.Single(findings);
    }

    [Fact(DisplayName = "Parent with a live variant convertible to the parent unit — clears")]
    public async Task Parent_LiveVariantConvertibleDifferentUnit_Clears()
    {
        var eachId = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
        var parent = new ProductFact(ParentId, "Miso Paste, White", true, GramId, IsParent: true);
        var liveVariant = new LiveVariantFact(HikariId, eachId);
        // Variant observed as 1 each (count) with a conversion each → gram of 125, so it converts into the
        // parent's gram reference unit and clears.
        var conversions = new Dictionary<Guid, IReadOnlyList<ConversionFact>>
        {
            [HikariId] = [new ConversionFact(HikariId, eachId, GramId, 125m)],
        };
        var observations = new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>
        {
            [HikariId] = [new PriceObservationFact(HikariId, 1.80m, 1m, eachId, 1.80m)],
        };

        var findings = await BuildDetector(
            ParentBagFor("Cacio e Pepe Vegan", parent, [liveVariant], observations, conversions)).DetectAsync();

        Assert.Empty(findings);
    }
}
