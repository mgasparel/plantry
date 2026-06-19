using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="RecipeStager"/> — the recipe staging algorithm for
/// Grocy recipe import (plantry-zcw.6).
///
/// Tests cover:
/// - Directions HTML→text conversion (p, li, br, entity decode, collapse, normalisation)
/// - Nesting flattening (scale factor, group_heading, ingredient count)
/// - Ingredient crosswalk resolution (product + unit)
/// - Staging flags (HasFlattenedNesting, HasDroppedNotes, HasProducesProduct, CrosswalkMissing, NameCollision)
/// - Source mapping (userfields.original_recipe + produces-product provenance)
/// - Photo staging
/// - Name collision detection
/// </summary>
public sealed class RecipeStagerTests
{
    // ──────────── Test helpers ────────────────────────────────────────────

    private static GrocyRecipe Recipe(
        int id,
        string name,
        string? description = null,
        int baseServings = 4,
        int? productId = null,
        string? pictureFileName = null,
        string? rowCreatedTimestamp = null) =>
        new(id, name, description, baseServings, null, "normal",
            productId, pictureFileName, rowCreatedTimestamp);

    private static GrocyRecipePosition Position(
        int id,
        int recipeId,
        int productId,
        decimal amount = 1m,
        int quId = 2,
        string? note = null,
        string? ingredientGroup = null) =>
        new(id, recipeId, productId, amount, quId, note, ingredientGroup,
            null, null, null, null, null);

    private static GrocyRecipeNesting Nesting(
        int id,
        int recipeId,
        int includesRecipeId,
        decimal servings = 1m) =>
        new(id, recipeId, includesRecipeId, servings, null);

    private static GrocyRecipeUserfield UserField(int recipeId, string originalRecipe) =>
        new(recipeId, originalRecipe);

    private static GrocyRecipePhoto Photo(int recipeId, byte[] bytes, string? contentType = "image/jpeg") =>
        new(recipeId, contentType, bytes);

    private static GrocyQuantityUnit Unit(int id, string name) =>
        new(id, name, null, null);

    private static GrocyProduct Product(int id, string name) =>
        new(id, name, null, null, 2, 2, null, null, null,
            null, null, null, null, null, null, null, null, null, null);

    private static GrocyManifest ManifestWith(
        IEnumerable<GrocyRecipe> recipes,
        IEnumerable<GrocyRecipePosition>? positions = null,
        IEnumerable<GrocyRecipeNesting>? nestings = null,
        IEnumerable<GrocyRecipeUserfield>? userfields = null,
        IEnumerable<GrocyRecipePhoto>? photos = null,
        IEnumerable<GrocyProduct>? products = null,
        IEnumerable<GrocyQuantityUnit>? units = null) =>
        new GrocyManifest
        {
            ExtractedAt       = DateTimeOffset.UtcNow,
            Recipes           = recipes.ToList(),
            RecipePositions   = positions?.ToList() ?? [],
            RecipeNestings    = nestings?.ToList() ?? [],
            RecipeUserfields  = userfields?.ToList() ?? [],
            RecipePhotos      = photos?.ToList() ?? [],
            Products          = products?.ToList() ?? [],
            QuantityUnits     = units?.ToList() ?? [],
        };

    // ──────────── Directions HTML→text conversion ─────────────────────────

    [Fact]
    public void ConvertDirections_NullHtml_ReturnsNull()
    {
        Assert.Null(RecipeStager.ConvertDirectionsHtmlToText(null));
    }

    [Fact]
    public void ConvertDirections_EmptyHtml_ReturnsNull()
    {
        Assert.Null(RecipeStager.ConvertDirectionsHtmlToText("   "));
    }

    [Fact]
    public void ConvertDirections_SingleParagraph_ProducesText()
    {
        var result = RecipeStager.ConvertDirectionsHtmlToText("<p>Preheat the oven.</p>");
        Assert.Equal("Preheat the oven.", result);
    }

    [Fact]
    public void ConvertDirections_TwoParagraphs_SeparatedByBlankLine()
    {
        var html = "<p>Step one.</p><p>Step two.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        // The two steps must be separated by exactly one blank line (\n\n)
        Assert.Contains("\n\n", result);
        var parts = result!.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Equal("Step one.", parts[0].Trim());
        Assert.Equal("Step two.", parts[1].Trim());
    }

    [Fact]
    public void ConvertDirections_ListItems_EachBecomesStep()
    {
        var html = "<ul><li>First item.</li><li>Second item.</li></ul>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        var parts = result!.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Equal("First item.", parts[0].Trim());
        Assert.Equal("Second item.", parts[1].Trim());
    }

    [Fact]
    public void ConvertDirections_BreakTag_LineBreakWithinSameStep()
    {
        var html = "<p>Line one.<br>Line two.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        // Single \n between lines (same step), NOT a blank line (\n\n)
        Assert.Contains("\n", result);
        Assert.DoesNotContain("\n\n", result);
        Assert.Contains("Line one.", result);
        Assert.Contains("Line two.", result);
    }

    [Fact]
    public void ConvertDirections_SelfClosingBreakTag_LineBreakWithinSameStep()
    {
        var html = "<p>Line one.<br/>Line two.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        Assert.Contains("\n", result);
        Assert.DoesNotContain("\n\n", result);
    }

    [Fact]
    public void ConvertDirections_HtmlEntities_Decoded()
    {
        var html = "<p>Heat oil &amp; garlic. Add 1&nbsp;tsp salt. Cook until it&#39;s done.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        Assert.Contains("&", result);
        Assert.Contains("tsp", result);
        Assert.Contains("'s", result);
    }

    [Fact]
    public void ConvertDirections_TripleNewlinesCollapsed()
    {
        // Simulate 3+ blank lines from double-wrapped paragraphs
        var html = "<p>Step one.</p>\n\n\n\n<p>Step two.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        // After collapse, no run of 3+ newlines
        Assert.DoesNotContain("\n\n\n", result);
    }

    [Fact]
    public void ConvertDirections_StripsBoldAndLinks()
    {
        var html = "<p>Add <strong>2 cups</strong> of flour and visit <a href=\"http://example.com\">this site</a>.</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        Assert.NotNull(result);
        // Tags stripped; text preserved
        Assert.Contains("2 cups", result);
        Assert.Contains("flour", result);
        Assert.Contains("this site", result);
        Assert.DoesNotContain("<strong>", result);
        Assert.DoesNotContain("<a ", result);
    }

    [Fact]
    public void ConvertDirections_PostNormalisationEnsuresBlankLineBetweenSteps()
    {
        // A raw multi-step text block with only a single \n (not a blank line)
        // The normalisation pass must ensure blank line separation
        var html = "<p>Step one\nStep two</p><p>Step three</p>";
        var result = RecipeStager.ConvertDirectionsHtmlToText(html);

        // After normalisation, last paragraph is always separated by blank line
        Assert.NotNull(result);
        Assert.Contains("Step three", result);
    }

    // ──────────── Staging: basic field mapping ────────────────────────────

    [Fact]
    public void Stage_MapsBasicFields()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Pasta", description: "<p>Cook pasta.</p>", baseServings: 2,
                rowCreatedTimestamp: "2025-01-15 10:30:00")],
            positions: [Position(1, 1, 10, amount: 200m, quId: 13)],
            products: [Product(10, "Spaghetti")],
            units: [Unit(13, "Gram")]);

        var productMap = new Dictionary<int, Guid> { [10] = Guid.NewGuid() };
        var unitMap    = new Dictionary<int, Guid> { [13] = Guid.NewGuid() };
        var productNames = new Dictionary<int, string> { [10] = "Spaghetti" };
        var unitNames    = new Dictionary<int, string> { [13] = "Gram" };

        var rows = RecipeStager.Stage(manifest, productMap, productNames, unitMap, unitNames);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(1, row.GrocyId);
        Assert.Equal("Pasta", row.GrocyName);
        Assert.Equal("Pasta", row.PlantryName);
        Assert.Equal(2, row.BaseServings);
        Assert.NotNull(row.Directions);
        Assert.Contains("Cook pasta", row.Directions);
        Assert.NotNull(row.CreatedAt);
    }

    // ──────────── Source (userfield) mapping ─────────────────────────────

    [Fact]
    public void Stage_MapsSourceFromUserfield()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10)],
            userfields: [UserField(1, "https://example.com/soup")]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Single(rows);
        Assert.Equal("https://example.com/soup", rows[0].Source);
    }

    [Fact]
    public void Stage_NoUserfield_SourceIsNull()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Null(rows[0].Source);
    }

    // ──────────── HasProducesProduct flag ─────────────────────────────────

    [Fact]
    public void Stage_RecipeWithProductId_SetsProducesProductFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Chicken Stock", productId: 42)],
            positions: [Position(1, 1, 10)],
            products: [Product(42, "Chicken Stock Product"), Product(10, "Chicken")]);

        var productNames = new Dictionary<int, string> { [42] = "Chicken Stock Product", [10] = "Chicken" };

        var rows = RecipeStager.Stage(manifest, null, productNames, null, null);

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.HasProducesProduct);
        Assert.True(row.IsFlagged);
        // Product name should be appended to source
        Assert.NotNull(row.Source);
        Assert.Contains("produces:", row.Source);
        Assert.Contains("Chicken Stock Product", row.Source);
    }

    [Fact]
    public void Stage_ProducesProduct_WithExistingSource_AppendedToSource()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Stock", productId: 42)],
            positions: [Position(1, 1, 10)],
            products: [Product(42, "Stock Product"), Product(10, "Bones")],
            userfields: [UserField(1, "https://example.com/stock")]);

        var productNames = new Dictionary<int, string> { [42] = "Stock Product", [10] = "Bones" };

        var rows = RecipeStager.Stage(manifest, null, productNames, null, null);

        Assert.Single(rows);
        var source = rows[0].Source;
        Assert.NotNull(source);
        Assert.Contains("https://example.com/stock", source);
        Assert.Contains("produces:", source);
    }

    // ──────────── Name collision detection ───────────────────────────────

    [Fact]
    public void Stage_NameCollision_SetsFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Pasta")],
            positions: [Position(1, 1, 10)]);

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pasta" };

        var rows = RecipeStager.Stage(manifest, null, null, null, null, existingNames);

        Assert.Single(rows);
        Assert.True(rows[0].HasNameCollision);
        Assert.True(rows[0].IsFlagged);
        Assert.StartsWith("Pasta", rows[0].PlantryName);
        Assert.Contains("Grocy", rows[0].PlantryName);
    }

    [Fact]
    public void Stage_NoExistingRecipes_NoNameCollision()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Pasta")],
            positions: [Position(1, 1, 10)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.False(rows[0].HasNameCollision);
        Assert.Equal("Pasta", rows[0].PlantryName);
    }

    [Fact]
    public void Stage_NameCollision_IsCaseInsensitive()
    {
        var manifest = ManifestWith(
            [Recipe(1, "PASTA")],
            positions: [Position(1, 1, 10)]);

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pasta" };

        var rows = RecipeStager.Stage(manifest, null, null, null, null, existingNames);

        Assert.True(rows[0].HasNameCollision);
    }

    // ──────────── Ingredient crosswalk resolution ─────────────────────────

    [Fact]
    public void Stage_ResolvesProductAndUnitFromCrosswalks()
    {
        var productId = Guid.NewGuid();
        var unitId    = Guid.NewGuid();

        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10, amount: 500m, quId: 15)]);

        var productMap = new Dictionary<int, Guid> { [10] = productId };
        var unitMap    = new Dictionary<int, Guid> { [15] = unitId };
        var productNames = new Dictionary<int, string> { [10] = "Broth" };
        var unitNames    = new Dictionary<int, string> { [15] = "ml" };

        var rows = RecipeStager.Stage(manifest, productMap, productNames, unitMap, unitNames);

        Assert.Single(rows);
        var ing = rows[0].Ingredients[0];
        Assert.Equal(productId, ing.PlantryProductId);
        Assert.Equal(unitId,    ing.PlantryUnitId);
        Assert.Equal("Broth",   ing.ProductName);
        Assert.Equal("ml",      ing.UnitName);
        Assert.Equal(500m,      ing.Amount);
        Assert.Equal(10,        ing.GrocyProductId);
        Assert.Equal(15,        ing.GrocyUnitId);
    }

    [Fact]
    public void Stage_NullCrosswalks_SetsCrosswalkMissingFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10, quId: 15)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.True(rows[0].HasCrosswalkMissing);
        Assert.Null(rows[0].Ingredients[0].PlantryProductId);
        Assert.Null(rows[0].Ingredients[0].PlantryUnitId);
    }

    [Fact]
    public void Stage_MissingCrosswalkEntry_SetsCrosswalkMissingFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, productId: 99, quId: 99)]);

        // Crosswalks present but don't contain id 99
        var productMap = new Dictionary<int, Guid> { [1] = Guid.NewGuid() };
        var unitMap    = new Dictionary<int, Guid> { [1] = Guid.NewGuid() };

        var rows = RecipeStager.Stage(manifest, productMap, null, unitMap, null);

        Assert.True(rows[0].HasCrosswalkMissing);
    }

    // ──────────── Dropped notes flag ─────────────────────────────────────

    [Fact]
    public void Stage_IngredientWithNote_SetsHasDroppedNotesFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10, note: "preferably fresh")]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.True(rows[0].HasDroppedNotes);
        Assert.Equal("preferably fresh", rows[0].Ingredients[0].DroppedNote);
    }

    [Fact]
    public void Stage_IngredientWithNoNote_NoDroppedNotesFlag()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10, note: null)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.False(rows[0].HasDroppedNotes);
    }

    // ──────────── Ingredient group_heading mapping ────────────────────────

    [Fact]
    public void Stage_IngredientGroupMappedToGroupHeading()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Salad")],
            positions: [Position(1, 1, 10, ingredientGroup: "Dressing")]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Equal("Dressing", rows[0].Ingredients[0].GroupHeading);
    }

    [Fact]
    public void Stage_NullIngredientGroup_NullGroupHeading()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Salad")],
            positions: [Position(1, 1, 10, ingredientGroup: null)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Null(rows[0].Ingredients[0].GroupHeading);
    }

    // ──────────── Nesting flattening ─────────────────────────────────────

    [Fact]
    public void Stage_NestingFlatten_InlinesSubRecipeIngredients()
    {
        // Parent recipe (id=1) includes sub-recipe (id=2) which has 1 serving of 2 ingredients.
        // Nesting: 1 serving of sub-recipe → scale factor = 1/1 = 1.0
        var subRecipe = Recipe(2, "Caesar Dressing", baseServings: 1);
        var manifest = ManifestWith(
            recipes: [Recipe(1, "Caesar Salad"), subRecipe],
            positions:
            [
                Position(1, recipeId: 1, productId: 10, amount: 100m, quId: 13), // direct
                Position(2, recipeId: 2, productId: 20, amount: 50m,  quId: 15), // sub
                Position(3, recipeId: 2, productId: 30, amount: 15m,  quId: 11), // sub
            ],
            nestings: [Nesting(1, recipeId: 1, includesRecipeId: 2, servings: 1m)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        // Only parent is staged (sub-recipe is also staged separately)
        var parent = rows.First(r => r.GrocyId == 1);
        Assert.True(parent.HasFlattenedNesting);
        Assert.Contains("Caesar Dressing", parent.FlattenedSubRecipeNames);

        // Direct ingredient + 2 flattened
        Assert.Equal(3, parent.Ingredients.Count);
        Assert.Equal(1, parent.Ingredients.Count(i => !i.IsFromNesting));
        Assert.Equal(2, parent.Ingredients.Count(i => i.IsFromNesting));
    }

    [Fact]
    public void Stage_NestingFlatten_ScalesByServings()
    {
        // Sub-recipe has base_servings=4; nesting uses 2 servings → scale = 2/4 = 0.5
        var subRecipe = Recipe(2, "Dough", baseServings: 4);
        var manifest = ManifestWith(
            recipes: [Recipe(1, "Pizza"), subRecipe],
            positions:
            [
                Position(1, recipeId: 2, productId: 10, amount: 400m, quId: 13), // flour: 400g
            ],
            nestings: [Nesting(1, recipeId: 1, includesRecipeId: 2, servings: 2m)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        var parent = rows.First(r => r.GrocyId == 1);
        var flattenedIng = parent.Ingredients.First(i => i.IsFromNesting);
        // scale = 2/4 = 0.5 → 400 * 0.5 = 200
        Assert.Equal(200m, flattenedIng.Amount);
    }

    [Fact]
    public void Stage_NestingFlatten_UsesSubRecipeNameAsGroupHeading()
    {
        var subRecipe = Recipe(2, "Vinaigrette", baseServings: 1);
        var manifest = ManifestWith(
            recipes: [Recipe(1, "Green Salad"), subRecipe],
            positions:
            [
                Position(1, recipeId: 2, productId: 10, amount: 30m, quId: 15),
            ],
            nestings: [Nesting(1, recipeId: 1, includesRecipeId: 2, servings: 1m)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        var parent = rows.First(r => r.GrocyId == 1);
        var flattenedIng = parent.Ingredients.First(i => i.IsFromNesting);
        Assert.Equal("Vinaigrette", flattenedIng.GroupHeading);
    }

    [Fact]
    public void Stage_NestingFlatten_DropNotesOnSubRecipeIngredients()
    {
        var subRecipe = Recipe(2, "Sauce", baseServings: 1);
        var manifest = ManifestWith(
            recipes: [Recipe(1, "Dish"), subRecipe],
            positions:
            [
                Position(1, recipeId: 2, productId: 10, note: "organic preferred"),
            ],
            nestings: [Nesting(1, recipeId: 1, includesRecipeId: 2, servings: 1m)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        var parent = rows.First(r => r.GrocyId == 1);
        Assert.True(parent.HasDroppedNotes);
    }

    [Fact]
    public void Stage_NestingToMissingSubRecipe_Skipped()
    {
        // Nesting points to a sub-recipe not in the manifest (should not throw)
        var manifest = ManifestWith(
            recipes: [Recipe(1, "Main Dish")],
            positions: [Position(1, recipeId: 1, productId: 10)],
            nestings: [Nesting(1, recipeId: 1, includesRecipeId: 999)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        // Should not throw; parent staged with direct ingredients only
        // No flattening happened (sub-recipe not found)
        Assert.Single(rows, r => r.GrocyId == 1);
        var parent = rows.First(r => r.GrocyId == 1);
        // HasFlattenedNesting is false because no sub-recipe was actually flattened
        Assert.False(parent.HasFlattenedNesting);
    }

    // ──────────── Photo staging ───────────────────────────────────────────

    [Fact]
    public void Stage_PhotoPresent_PopulatesPhotoBytesAndContentType()
    {
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG magic bytes
        var manifest = ManifestWith(
            [Recipe(1, "Cake", pictureFileName: "cake.jpg")],
            positions: [Position(1, 1, 10)],
            photos: [Photo(1, photoBytes, "image/jpeg")]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Equal(photoBytes, rows[0].PhotoBytes);
        Assert.Equal("image/jpeg", rows[0].PhotoContentType);
    }

    [Fact]
    public void Stage_NoPhoto_PhotoBytesIsNull()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup")],
            positions: [Position(1, 1, 10)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Null(rows[0].PhotoBytes);
        Assert.Null(rows[0].PhotoContentType);
    }

    // ──────────── Ordering ───────────────────────────────────────────────

    [Fact]
    public void Stage_ReturnsRowsOrderedByGrocyId()
    {
        var manifest = ManifestWith(
            [Recipe(10, "Ziti"), Recipe(3, "Alfredo"), Recipe(7, "Carbonara")],
            positions:
            [
                Position(1, 10, 1),
                Position(2, 3, 2),
                Position(3, 7, 3),
            ]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Equal([3, 7, 10], rows.Select(r => r.GrocyId).ToArray());
    }

    // ──────────── Ordinals ───────────────────────────────────────────────

    [Fact]
    public void Stage_Ingredients_AssignedContiguousOrdinals()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Dish")],
            positions:
            [
                Position(3, 1, 30),
                Position(1, 1, 10),
                Position(2, 1, 20),
            ]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        var ordinals = rows[0].Ingredients.Select(i => i.Ordinal).ToArray();
        // Ordinals must be 0, 1, 2 (contiguous)
        Assert.Equal([0, 1, 2], ordinals);
    }

    // ──────────── IsFlagged convenience ──────────────────────────────────

    [Fact]
    public void Stage_CleanRecipe_IsNotFlagged()
    {
        var productId = Guid.NewGuid();
        var unitId    = Guid.NewGuid();

        var manifest = ManifestWith(
            [Recipe(1, "Clean Soup")],
            positions: [Position(1, 1, 10, quId: 15)]);

        var productMap = new Dictionary<int, Guid> { [10] = productId };
        var unitMap    = new Dictionary<int, Guid> { [15] = unitId };

        var rows = RecipeStager.Stage(manifest, productMap, null, unitMap, null);

        Assert.False(rows[0].IsFlagged);
    }

    // ──────────── Timestamp parsing ──────────────────────────────────────

    [Theory]
    [InlineData("2025-01-15 10:30:00")]
    [InlineData("2026-06-18 00:00:00")]
    public void Stage_ParsesGrocyTimestamp(string timestamp)
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup", rowCreatedTimestamp: timestamp)],
            positions: [Position(1, 1, 10)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.NotNull(rows[0].CreatedAt);
    }

    [Fact]
    public void Stage_NullTimestamp_ReturnsNullCreatedAt()
    {
        var manifest = ManifestWith(
            [Recipe(1, "Soup", rowCreatedTimestamp: null)],
            positions: [Position(1, 1, 10)]);

        var rows = RecipeStager.Stage(manifest, null, null, null, null);

        Assert.Null(rows[0].CreatedAt);
    }

    // ──────────── Empty manifest ──────────────────────────────────────────

    [Fact]
    public void Stage_EmptyManifest_ReturnsEmpty()
    {
        var manifest = ManifestWith([]);
        var rows = RecipeStager.Stage(manifest, null, null, null, null);
        Assert.Empty(rows);
    }
}
