using Microsoft.Extensions.Logging.Abstractions;
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
        var service = new AuthorRecipe(recipes, tags, products, writer, converter, Clock, tenant, NullLogger<AuthorRecipe>.Instance);
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

    // ── Tracked-product inline create (plantry-orix) ─────────────────────────────

    [Fact]
    public async Task Create_Inline_Tracked_Standalone_Creates_Tracked_Product_And_Ingredient()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();

        // NewIsTracked = true, no group fields → standalone tracked product (CreateProductCommand).
        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Pasta Dish",
            DefaultServings: 2,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: 200m, UnitId: unit, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Olive Oil", NewStapleDefaultUnitId: unit,
                NewIsTracked: true)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        // Must route to CreateTrackedProductAsync, not CreateUntrackedStapleAsync.
        Assert.Empty(h.Writer.StaplesCreated);
        var created = Assert.Single(h.Writer.TrackedProductsCreated);
        Assert.Equal("Olive Oil", created.Name);
        Assert.Equal(unit, created.DefaultUnitId);
        // The ingredient row must reference the new tracked product.
        var recipe = Assert.Single(h.Recipes.Items);
        var ing = Assert.Single(recipe.Ingredients);
        Assert.NotEqual(Guid.Empty, ing.ProductId);
        Assert.Equal(200m, ing.Quantity);
    }

    [Fact]
    public async Task Create_Inline_Tracked_Variant_Creates_Variant_And_Ingredient()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var parentGroupId = Guid.NewGuid();

        // NewIsTracked = true, NewGroupId non-empty → CreateVariantCommand.
        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Salad",
            DefaultServings: 1,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: 100m, UnitId: unit, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Cherry Tomato", NewStapleDefaultUnitId: unit,
                NewIsTracked: true, NewGroupId: parentGroupId.ToString())],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.Empty(h.Writer.StaplesCreated);
        Assert.Empty(h.Writer.TrackedProductsCreated);
        var variant = Assert.Single(h.Writer.VariantsCreated);
        Assert.Equal(parentGroupId, variant.ParentGroupId);
        Assert.Equal("Cherry Tomato", variant.VariantName);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Single(recipe.Ingredients);
    }

    [Fact]
    public async Task Create_Inline_Tracked_Grouped_Product_Creates_Group_And_Variant_And_Ingredient()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();

        // NewIsTracked = true, NewGroupName non-empty → CreateGroupedProductCommand.
        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Tomato Sauce",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: 400m, UnitId: unit, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Roma Tomato", NewStapleDefaultUnitId: unit,
                NewIsTracked: true, NewGroupName: "Tomatoes")],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.Empty(h.Writer.StaplesCreated);
        Assert.Empty(h.Writer.TrackedProductsCreated);
        Assert.Empty(h.Writer.VariantsCreated);
        var grouped = Assert.Single(h.Writer.GroupedProductsCreated);
        Assert.Equal("Tomatoes", grouped.GroupName);
        Assert.Equal("Roma Tomato", grouped.VariantName);
        Assert.Equal(unit, grouped.DefaultUnitId);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.Single(recipe.Ingredients);
    }

    [Fact]
    public async Task Tracked_Inline_Product_Requires_Quantity_And_Unit_R5()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();

        // A tracked inline product must still have qty + unit (R5).
        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Test Recipe",
            DefaultServings: 1,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Butter", NewStapleDefaultUnitId: unit,
                NewIsTracked: true)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        // R5: tracked ingredient must have both quantity and unit — reject.
        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.TrackedRequiresQuantity", invalid.Error.Code);
        Assert.Empty(h.Recipes.Items);
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
    public async Task Rejects_Tracked_Line_With_Null_Quantity_Message_Names_Product_And_Line_R5()
    {
        // plantry-429l: the R5 rejection must name WHICH row is broken (product + 1-based line number)
        // so the editor can surface the offending line instead of an opaque global error. The error CODE
        // stays Recipes.TrackedRequiresQuantity (existing handling/tests key on it).
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var okProduct = h.Products.AddTracked(unit, "Flour");
        var saltId = Guid.CreateVersion7();
        // A product that is now tracked but referenced with null qty/unit (untracked→tracked flip).
        h.Products.RegisterTracked(saltId, "Sea salt");

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Bread", DefaultServings: 2,
            Lines:
            [
                new AuthorIngredientLine(okProduct.Id, 200m, unit, null, 0),
                new AuthorIngredientLine(saltId, Quantity: null, UnitId: null, null, 1),
            ],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.TrackedRequiresQuantity", invalid.Error.Code);
        Assert.Contains("Sea salt", invalid.Error.Description);
        // 1-based line number — the offending line is the second row (Ordinal 1).
        Assert.Contains("line 2", invalid.Error.Description);
        Assert.Empty(h.Recipes.Items);
    }

    [Fact]
    public async Task Rejects_Inline_Tracked_Product_Null_Quantity_Message_Names_Product_And_Line_R5()
    {
        // plantry-429l: the inline tracked-create pre-check message is enriched the same way for consistency.
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Test Recipe", DefaultServings: 1,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Butter", NewStapleDefaultUnitId: unit, NewIsTracked: true)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.TrackedRequiresQuantity", invalid.Error.Code);
        Assert.Contains("Butter", invalid.Error.Description);
        Assert.Contains("line 1", invalid.Error.Description);
        Assert.Empty(h.Recipes.Items);
    }

    [Fact]
    public async Task Saves_Existing_Untracked_Staple_With_Null_Quantity_R5()
    {
        // Regression for plantry-4udr: a seed recipe references an *existing* untracked staple
        // (e.g. Sea salt) as a "to taste" line — a resolved product id with null qty + unit. Once
        // the seeder mints such staples trackStock:false, this shape must load and save through
        // AuthorRecipe without tripping R5 (which only fires for a TRACKED product with null qty).
        var h = BuildHarness();
        var trackedUnit = Guid.CreateVersion7();
        var tracked = h.Products.AddTracked(trackedUnit, "Chicken breast");
        var saltId = Guid.CreateVersion7();
        h.Products.RegisterUntracked(saltId, "Sea salt");

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Chicken & Chickpea Curry", DefaultServings: 4,
            Lines:
            [
                new AuthorIngredientLine(tracked.Id, 600m, trackedUnit, null, 0),
                // "to taste" — null qty + unit, permitted because Sea salt is untracked.
                new AuthorIngredientLine(saltId, Quantity: null, UnitId: null, null, 1),
            ],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items);
        var salt = recipe.Ingredients.Single(i => i.ProductId == saltId);
        Assert.Null(salt.Quantity);
        Assert.Null(salt.UnitId);
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

    /// <summary>
    /// plantry-qno9: when the line carries an explicit conversion unit pair (the four-field equation —
    /// e.g. "1 kg = 8 cups"), the service writes THAT pair verbatim (from = left unit, to = right unit,
    /// factor = supplied), not the legacy recipeUnit→productDefault assumption. The recipe-line unit
    /// (cup) still differs from the product default (g), so a path is required; here the re-check path is
    /// pre-seeded so the save completes, letting the test assert the exact written triple.
    /// </summary>
    [Fact]
    public async Task NeedsConversion_Honours_Explicit_From_To_Unit_Pair()
    {
        var h = BuildHarness();
        var gram = Guid.CreateVersion7();   // product default (stock)
        var kilogram = Guid.CreateVersion7(); // LEFT unit (stock dimension), not the default
        var cup = Guid.CreateVersion7();     // RIGHT unit = the recipe line unit (recipe dimension)
        var product = h.Products.AddTracked(gram);

        // Pre-seed the recipe-line unit → product-default path so the post-write re-check (cup → g) passes
        // once the author's conversion has been applied — isolating the assertion to the WRITTEN triple.
        h.Converter.AddPath(product.Id, cup, gram, 125m);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Cashew Bowl", DefaultServings: 4,
            Lines:
            [
                new AuthorIngredientLine(
                    product.Id, 2m, cup, null, 0,
                    ConversionFactor: 8m,
                    ConversionFromUnitId: kilogram,
                    ConversionToUnitId: cup),
            ],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        // The verbatim author fact "1 kg = 8 cup" is stored — NOT (cup → g).
        var written = Assert.Single(h.Writer.ConversionsAdded);
        Assert.Equal((product.Id, kilogram, cup, 8m), written);
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
