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
        var lineResolver = new IngredientLineResolver(products, writer);
        var conversionPlanner = new ConversionGapPlanner(converter, writer);
        var service = new AuthorRecipe(recipes, tags, products, writer, lineResolver, conversionPlanner, Clock, tenant, NullLogger<AuthorRecipe>.Instance);
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
    public async Task Create_Inline_Tracked_Stores_Independent_Recipe_Unit_Distinct_From_Product_Default()
    {
        // plantry-dtr9: the inline-create flow must let the recipe line carry its OWN unit, independent of
        // the minted product's default/stock unit — e.g. stock olive oil in ml, use it by the tbsp. Before
        // the fix the create view had no recipe-unit field, so the line silently inherited the product
        // default. This pins the contract that AuthorRecipe stores the LINE's UnitId (not the default) and
        // routes an author-supplied cross-dimension factor to the freshly minted product.
        var h = BuildHarness();
        var recipeUnit = Guid.CreateVersion7(); // what THIS recipe measures in (e.g. tbsp / g)
        var stockUnit = Guid.CreateVersion7();  // the product default/stock unit (e.g. ml / ea)

        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Salad Dressing",
            DefaultServings: 2,
            Lines: [new AuthorIngredientLine(
                ProductId: null, Quantity: 2m, UnitId: recipeUnit, GroupHeading: null, Ordinal: 0,
                NewStapleName: "Olive Oil", NewStapleDefaultUnitId: stockUnit,
                // Author supplies the four-field factor so the cross-dimension pair resolves on this pass
                // (a same-dimension pair would resolve universally with no factor; the fake converter is
                // path-based, so the factor stands in for that universal bridge here).
                ConversionFactor: 15m, NewIsTracked: true,
                ConversionFromUnitId: recipeUnit, ConversionToUnitId: stockUnit)],
            TagIds: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        // The product is minted with the STOCK unit as its default.
        var created = Assert.Single(h.Writer.TrackedProductsCreated);
        Assert.Equal(stockUnit, created.DefaultUnitId);
        // The ingredient line stores the RECIPE unit — NOT coerced to the product default.
        var recipe = Assert.Single(h.Recipes.Items);
        var ing = Assert.Single(recipe.Ingredients);
        Assert.Equal(recipeUnit, ing.UnitId);
        Assert.NotEqual(stockUnit, ing.UnitId);
        Assert.Equal(2m, ing.Quantity);
        // The author-supplied factor is written against the freshly minted product (recipeUnit → stockUnit).
        var conv = Assert.Single(h.Writer.ConversionsAdded);
        Assert.Equal(ing.ProductId, conv.ProductId);
        Assert.Equal(recipeUnit, conv.FromUnitId);
        Assert.Equal(stockUnit, conv.ToUnitId);
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
    /// plantry-qll2.4: when the command opts into deferral (edit-moment AI assistance on + a seeder
    /// available), a cross-dimension unit gap no longer blocks the save. The recipe is persisted WITH the
    /// gap and the gap is carried out on <see cref="AuthorRecipeResult.Saved.DeferredConversions"/> (line
    /// unit → product default) so the caller can seed an ai_suggested factor asynchronously — instead of
    /// the NeedsConversion prompt. No conversion is written inline.
    /// </summary>
    [Fact]
    public async Task DeferMissingConversions_Saves_With_Gap_And_Reports_Deferred_Instead_Of_Blocking()
    {
        var h = BuildHarness();
        var defaultUnit = Guid.CreateVersion7();
        var lineUnit = Guid.CreateVersion7(); // cross-dimension: no path seeded on the fake converter
        var product = h.Products.AddTracked(defaultUnit);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Cashew Bowl", DefaultServings: 2,
            Lines: [new AuthorIngredientLine(product.Id, 1m, lineUnit, null, 0)],
            TagIds: [],
            DeferMissingConversions: true);

        var result = await h.Service.ExecuteAsync(command);

        var saved = Assert.IsType<AuthorRecipeResult.Saved>(result);
        var deferred = Assert.Single(saved.DeferredConversions);
        Assert.Equal(0, deferred.Ordinal);
        Assert.Equal(product.Id, deferred.ProductId);
        Assert.Equal(lineUnit, deferred.FromUnitId);
        Assert.Equal(defaultUnit, deferred.ToUnitId);
        Assert.Single(h.Recipes.Items);           // recipe persisted despite the gap
        Assert.Empty(h.Writer.ConversionsAdded);  // no inline factor written — that is the async seed's job
    }

    /// <summary>
    /// plantry-qll2.4: a saved recipe with no unit gap reports no deferred conversions even when deferral
    /// is enabled — the flag only changes behaviour when a gap actually exists (a same-unit line converts
    /// trivially on the fake converter, so nothing is missing).
    /// </summary>
    [Fact]
    public async Task DeferMissingConversions_With_No_Gap_Reports_No_Deferred()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Simple", DefaultServings: 1,
            Lines: [new AuthorIngredientLine(product.Id, 100m, unit, null, 0)],
            TagIds: [],
            DeferMissingConversions: true);

        var result = await h.Service.ExecuteAsync(command);

        var saved = Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.Empty(saved.DeferredConversions);
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

    // ── Inclusions & N4 (recipe-composition.md) ──────────────────────────────────

    /// <summary>Persists a recipe (one plain ingredient) directly into the fake repo for graph setup.</summary>
    private Recipe SeedRecipe(Harness h, string name, int servings = 4)
    {
        var recipe = Recipe.Create(Household, name, servings, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0)], Clock);
        h.Recipes.Items.Add(recipe);
        return recipe;
    }

    [Fact]
    public async Task Create_Inclusions_Only_Recipe_Saves()
    {
        var h = BuildHarness();
        var sub = SeedRecipe(h, "Nacho Cheese");

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Nachos Deluxe", DefaultServings: 2,
            Lines: [], TagIds: [],
            Inclusions: [new AuthorInclusionLine(sub.Id.Value, 2m, null, 0)]);

        var result = await h.Service.ExecuteAsync(command);

        var saved = Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = h.Recipes.Items.Single(r => r.Id == saved.RecipeId);
        Assert.Empty(recipe.Ingredients);
        var inc = Assert.Single(recipe.Inclusions);
        Assert.Equal(sub.Id, inc.SubRecipeId);
        Assert.Equal(2m, inc.Servings);
    }

    [Fact]
    public async Task Inclusion_Of_Unknown_SubRecipe_Is_Rejected()
    {
        var h = BuildHarness();

        var command = new AuthorRecipeCommand(
            RecipeId: null, Name: "Broken", DefaultServings: 1,
            Lines: [], TagIds: [],
            Inclusions: [new AuthorInclusionLine(Guid.NewGuid(), 1m, null, 0)]);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.UnknownSubRecipe", invalid.Error.Code);
    }

    [Fact]
    public async Task N4_Direct_Cycle_Is_Rejected()
    {
        // A includes B; editing B to include A closes a direct cycle A→B→A.
        var h = BuildHarness();
        var a = SeedRecipe(h, "A");
        var b = SeedRecipe(h, "B");
        a.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(b.Id, 1m, null, 0)], a.Id).Value, Clock);

        var command = new AuthorRecipeCommand(
            RecipeId: b.Id, Name: "B", DefaultServings: 4,
            Lines: [], TagIds: [],
            Inclusions: [new AuthorInclusionLine(a.Id.Value, 1m, null, 0)]);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.InclusionCycle", invalid.Error.Code);
    }

    [Fact]
    public async Task N4_Transitive_Cycle_Is_Rejected()
    {
        // A→B and B→C exist; editing C to include A closes a transitive cycle A→B→C→A.
        var h = BuildHarness();
        var a = SeedRecipe(h, "A");
        var b = SeedRecipe(h, "B");
        var c = SeedRecipe(h, "C");
        a.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(b.Id, 1m, null, 0)], a.Id).Value, Clock);
        b.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(c.Id, 1m, null, 0)], b.Id).Value, Clock);

        var command = new AuthorRecipeCommand(
            RecipeId: c.Id, Name: "C", DefaultServings: 4,
            Lines: [], TagIds: [],
            Inclusions: [new AuthorInclusionLine(a.Id.Value, 1m, null, 0)]);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.InclusionCycle", invalid.Error.Code);
    }

    [Fact]
    public async Task N4_Acyclic_Chain_Extension_Is_Accepted()
    {
        // A→B exists; adding B→C is a longer chain but NOT a cycle — must save.
        var h = BuildHarness();
        var a = SeedRecipe(h, "A");
        var b = SeedRecipe(h, "B");
        var c = SeedRecipe(h, "C");
        a.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(b.Id, 1m, null, 0)], a.Id).Value, Clock);

        var command = new AuthorRecipeCommand(
            RecipeId: b.Id, Name: "B", DefaultServings: 4,
            Lines: [], TagIds: [],
            Inclusions: [new AuthorInclusionLine(c.Id.Value, 1m, null, 0)]);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.Equal(c.Id, Assert.Single(b.Inclusions).SubRecipeId);
    }

    // ── Yield-on-cook (plantry-854a, recipe-composition.md §9) ────────────────

    [Fact]
    public async Task Create_With_Yield_Enabled_Auto_Creates_Tracked_Product_From_Recipe_Name()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        var yieldUnit = Guid.CreateVersion7();

        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Nacho Cheese",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 200m, unit, null, 0)],
            TagIds: [],
            YieldEnabled: true,
            YieldQuantity: 6m,
            YieldUnitId: yieldUnit);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        var recipe = Assert.Single(h.Recipes.Items);
        Assert.True(recipe.HasYield);
        Assert.Equal(6m, recipe.YieldQuantity);
        Assert.Equal(yieldUnit, recipe.YieldUnitId);
        // The yield product was auto-created from the recipe name via ICatalogWriter.
        var created = Assert.Single(h.Writer.TrackedProductsCreated);
        Assert.Equal("Nacho Cheese", created.Name);
        Assert.Equal(yieldUnit, created.DefaultUnitId);
        Assert.NotNull(recipe.YieldProductId);
    }

    [Fact]
    public async Task Edit_Reenabling_Yield_Reuses_Existing_Same_Named_Product_Not_A_Duplicate()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);
        var yieldUnit = Guid.CreateVersion7();
        // A tracked product already named after the recipe exists in the catalog.
        var existingYield = h.Products.AddTracked(yieldUnit, name: "Pie Crust");

        var recipe = Recipe.Create(Household, "Pie Crust", 4, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(product.Id, 100m, unit, null, 0)], Clock);
        h.Recipes.Items.Add(recipe);

        var command = new AuthorRecipeCommand(
            RecipeId: recipe.Id,
            Name: "Pie Crust",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 100m, unit, null, 0)],
            TagIds: [],
            YieldEnabled: true,
            YieldQuantity: 2m,
            YieldUnitId: yieldUnit);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        // Reused the existing same-named product; no duplicate create.
        Assert.Empty(h.Writer.TrackedProductsCreated);
        Assert.Equal(existingYield.Id, recipe.YieldProductId);
    }

    [Fact]
    public async Task Edit_With_Yield_Disabled_Clears_The_Yield()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);

        var recipe = Recipe.Create(Household, "Soup", 4, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(product.Id, 100m, unit, null, 0)], Clock);
        recipe.SetYield(Guid.CreateVersion7(), 4m, Guid.CreateVersion7(), Clock);
        h.Recipes.Items.Add(recipe);

        var command = new AuthorRecipeCommand(
            RecipeId: recipe.Id,
            Name: "Soup",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 100m, unit, null, 0)],
            TagIds: [],
            YieldEnabled: false);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<AuthorRecipeResult.Saved>(result);
        Assert.False(recipe.HasYield);
    }

    [Fact]
    public async Task Create_With_Yield_Enabled_But_Missing_Unit_Is_Invalid()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unit);

        var command = new AuthorRecipeCommand(
            RecipeId: null,
            Name: "Broth",
            DefaultServings: 4,
            Lines: [new AuthorIngredientLine(product.Id, 100m, unit, null, 0)],
            TagIds: [],
            YieldEnabled: true,
            YieldQuantity: 4m,
            YieldUnitId: null);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<AuthorRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.MissingYieldUnit", invalid.Error.Code);
    }
}
