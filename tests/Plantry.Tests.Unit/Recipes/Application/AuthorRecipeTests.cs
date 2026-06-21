using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class AuthorRecipeTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required FakeRecipeRepository Recipes { get; init; }
        public required FakeTagRepository Tags { get; init; }
        public required FakeCatalogProductReader Products { get; init; }
        public required FakeCatalogWriter Writer { get; init; }
        public required FakeUnitConverter Converter { get; init; }
        public required AuthorRecipe Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var recipes = new FakeRecipeRepository();
        var tags = new FakeTagRepository();
        var products = new FakeCatalogProductReader();
        var converter = new FakeUnitConverter();
        var writer = new FakeCatalogWriter(products, converter);
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : null);
        var service = new AuthorRecipe(recipes, tags, products, writer, converter, Clock, tenant);
        return new Harness
        {
            Recipes = recipes, Tags = tags, Products = products,
            Writer = writer, Converter = converter, Service = service,
        };
    }

    private HouseholdId Household => HouseholdId.From(_householdGuid);

    // ── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_With_Tracked_Lines_Saves_Recipe_And_Resolves_Tag()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        // Pre-seed the household tag that the picker would submit.
        var existingTag = Tag.Create(Household, "Breakfast", null, Clock);
        h.Tags.Items.Add(existingTag);

        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Pancakes",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 200m, unit, null, 0)],
            TagIds: [existingTag.Id.Value]);

        var result = await h.Service.ExecuteAsync(command);

        var saved = Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Equal(saved.RecipeId, recipe.Id);
        Assert.Equal("Pancakes", recipe.Name);
        var ing = Assert.Single(recipe.Ingredients);
        Assert.Equal(product.Id, ing.ProductId);
        Assert.Equal(200m, ing.Quantity);
        // Resolve-only: the pre-seeded tag is applied; no new tag is minted.
        Assert.Single(h.Tags.Items); // still only the pre-seeded tag
        Assert.Equal(existingTag.Id, Assert.Single(recipe.Tags).TagId);
        Assert.Equal(1, h.Recipes.SaveChangesCalls);
    }

    [Fact]
    public async Task Create_With_Unknown_TagId_Drops_It_And_Does_Not_Mint()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        var foreignId = Guid.NewGuid(); // not in the repository

        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Toast",
            DefaultServings: 1,
            Lines: [new AuthorIngredientLine(product.Id, 1m, unit, null, 0)],
            TagIds: [foreignId]);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items);
        // Unknown id is silently dropped — no tag minted, recipe has no tags.
        Assert.Empty(h.Tags.Items);
        Assert.Empty(recipe.Tags);
    }

    [Fact]
    public async Task Create_Inline_Staple_Resolves_To_New_Untracked_Product_With_Null_Qty()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();

        // No ProductId — an inline untracked staple "to taste" (C12): null qty/unit is allowed (R5).
        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Soup",
            DefaultServings: 2,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Salt", NewStapleDefaultUnitId: unit)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var staple = Assert.Single(h.Writer.StaplesCreated);
        Assert.Equal("Salt", staple.Name);
        var recipe = Assert.Single(h.Recipes.Items);
        var ing = Assert.Single(recipe.Ingredients);
        Assert.NotEqual(Guid.Empty, ing.ProductId);
        Assert.Null(ing.Quantity);
        Assert.Null(ing.UnitId);
    }

    [Fact]
    public async Task Create_Resolves_Existing_Tag_By_Id()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        var existingTag = Tag.Create(Household, "Dinner", null, Clock);
        h.Tags.Items.Add(existingTag);

        // Picker submits the existing tag's id — must resolve and apply, not duplicate.
        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Roast", DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 1m, unit, null, 0)],
            TagIds: [existingTag.Id.Value]);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.Single(h.Tags.Items); // not duplicated
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Equal(existingTag.Id, Assert.Single(recipe.Tags).TagId);
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rejects_Duplicate_Recipe_Name()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        h.Recipes.Items.Add(Recipe.Create(Household, "Pancakes", 4, Clock).Value);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "pancakes", DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 1m, unit, null, 0)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.DuplicateName", invalid.Error.Code);
        Assert.Single(h.Recipes.Items); // nothing new persisted
    }

    [Fact]
    public async Task Rejects_Tracked_Line_With_Null_Quantity_R5()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Bread", DefaultServings: 2,
            Lines: [new AuthorIngredientLine(product.Id, Quantity: null, UnitId: null, null, 0)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.TrackedRequiresQuantity", invalid.Error.Code);
        Assert.Empty(h.Recipes.Items);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var h = BuildHarness(authenticated: false);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "X", DefaultServings: 1, Lines: [], TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Unauthorized", invalid.Error.Code);
    }

    // ── Unit-mismatch (R7 / C10) ─────────────────────────────────────────────────

    [Fact]
    public async Task NeedsConversion_When_No_Path_Then_Saved_When_Factor_Supplied()
    {
        var h = BuildHarness();
        var defaultUnit = Guid.CreateVersion7();
        var lineUnit = Guid.CreateVersion7(); // differs from product default → conversion required
        var product = h.Products.AddTracked(defaultUnit);

        var firstAttempt = new AuthorRecipeCommand(
            RecipeId: null, Name: "Cake", DefaultServings: 6,
            Lines: [new AuthorIngredientLine(product.Id, 2m, lineUnit, null, 0)],
            TagIds: []);

        var first = await h.Service.ExecuteAsync(firstAttempt);

        var needs = Assert.IsType<AuthorRecipeResult.NeedsConversion>(first);
        var ask = Assert.Single(needs.Conversions);
        Assert.Equal(0, ask.Ordinal);
        Assert.Equal(product.Id, ask.ProductId);
        Assert.Equal(lineUnit, ask.FromUnitId);
        Assert.Equal(defaultUnit, ask.ToUnitId);
        Assert.Empty(h.Recipes.Items); // save blocked

        // Author supplies the factor; the service writes it to Catalog and the retry path check passes.
        var retry = firstAttempt with
        {
            Lines = [new AuthorIngredientLine(product.Id, 2m, lineUnit, null, 0, ConversionFactor: 240m)],
        };

        var second = await h.Service.ExecuteAsync(retry);

        Assert.IsType<AuthorRecipeResult.Saved>(second);
        var written = Assert.Single(h.Writer.ConversionsAdded);
        Assert.Equal((product.Id, lineUnit, defaultUnit, 240m), written);
        Assert.Single(h.Recipes.Items);
    }

    // ── Ordinal assembly ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Assembles_Contiguous_Ordinals_From_Unordered_Input()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var first = h.Products.AddTracked(unit, "First");
        var second = h.Products.AddTracked(unit, "Second");
        var third = h.Products.AddTracked(unit, "Third");

        // Input ordinals are sparse and out of order; assembly must sort then renumber 0,1,2.
        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Salad", DefaultServings: 2,
            Lines:
            [
                new AuthorIngredientLine(third.Id, 3m, unit, null, 9),
                new AuthorIngredientLine(first.Id, 1m, unit, null, 2),
                new AuthorIngredientLine(second.Id, 2m, unit, null, 5),
            ],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items);
        var ordered = recipe.Ingredients.OrderBy(i => i.Ordinal).ToList();
        Assert.Equal([0, 1, 2], ordered.Select(i => i.Ordinal));
        Assert.Equal([first.Id, second.Id, third.Id], ordered.Select(i => i.ProductId));
    }

    // ── Edit + serving scale (J7 step 3) ─────────────────────────────────────────

    [Theory]
    [InlineData(ScaleMode.Proportional, 400, 200)]
    [InlineData(ScaleMode.Keep, 200, 100)]
    public async Task Edit_Servings_Change_Applies_ScaleMode(ScaleMode mode, int expectedA, int expectedB)
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var productA = h.Products.AddTracked(unit, "A");
        var productB = h.Products.AddTracked(unit, "B");

        var createId = await CreateStew(h, unit, productA, productB);

        var edit = new AuthorRecipeCommand(
            RecipeId: createId, Name: "Stew", DefaultServings: 8,
            Lines:
            [
                new AuthorIngredientLine(productA.Id, 200m, unit, null, 0),
                new AuthorIngredientLine(productB.Id, 100m, unit, null, 1),
            ],
            TagIds: [],
            ScaleMode: mode);

        var result = await h.Service.ExecuteAsync(edit);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items); // same aggregate, edited in place
        Assert.Equal(8, recipe.DefaultServings);
        Assert.Equal(expectedA, recipe.Ingredients.Single(i => i.ProductId == productA.Id).Quantity);
        Assert.Equal(expectedB, recipe.Ingredients.Single(i => i.ProductId == productB.Id).Quantity);
    }

    private static async Task<RecipeId> CreateStew(
        Harness h, Guid unit, CatalogProduct productA, CatalogProduct productB)
    {
        var create = new AuthorRecipeCommand(
            RecipeId: null, Name: "Stew", DefaultServings: 4,
            Lines:
            [
                new AuthorIngredientLine(productA.Id, 200m, unit, null, 0),
                new AuthorIngredientLine(productB.Id, 100m, unit, null, 1),
            ],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(create);
        return Assert.IsType<AuthorRecipeResult.Saved>(result).RecipeId;
    }
}
