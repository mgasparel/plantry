using System.Text.Json;
using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="GrocyManifest"/> serialization round-trip and
/// <see cref="ManifestCounts.From"/> — covering the Extract + display pipeline without
/// a live Grocy instance or web application factory.
/// </summary>
public sealed class GrocyManifestTests
{
    // Write options match ExtractCommand's private JsonOptions.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Read options match IndexModel's private ReadJsonOptions.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ManifestCounts_From_Returns_PerCollection_Counts()
    {
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            Products = [new GrocyProduct(1, "Milk", null, null, 1, 1, null, null, null, null, null, null, null, null, null, null, null, null, null)],
            QuantityUnits = [new GrocyQuantityUnit(1, "Gram", null, null), new GrocyQuantityUnit(2, "kg", null, null)],
            QuantityUnitConversions =
            [
                new GrocyQuantityUnitConversion(1, 1, 2, 1000m, null, null),
                new GrocyQuantityUnitConversion(2, 2, 1, 0.001m, null, null),
                new GrocyQuantityUnitConversion(3, 1, 2, 500m, 42, null),
            ],
            Locations = [new GrocyLocation(1, "Fridge", null, 0, null)],
            ProductGroups = [new GrocyProductGroup(1, "Dairy", null, null), new GrocyProductGroup(2, "Fruit", null, null)],
            Recipes =
            [
                new GrocyRecipe(1, "Salad", null, 2, null, "normal", null, null, null),
                new GrocyRecipe(2, "Soup", null, 4, null, "normal", null, null, null),
                new GrocyRecipe(3, "Casserole", null, 6, null, "normal", null, null, null),
            ],
            RecipePositions =
            [
                new GrocyRecipePosition(1, 1, 1, 100m, 1, null, null, null, null, null, null, null),
                new GrocyRecipePosition(2, 1, 1, 50m, 1, null, null, null, null, null, null, null),
            ],
            RecipeNestings = [new GrocyRecipeNesting(1, 1, 2, 1m, null)],
            Userfields = [new GrocyUserfield(1, "recipes", "original_recipe", "Source URL", "link", 1, null)],
            ProductBarcodes = [],
        };

        var counts = ManifestCounts.From(manifest);

        Assert.Equal(1, counts.Products);
        Assert.Equal(2, counts.QuantityUnits);
        Assert.Equal(3, counts.QuantityUnitConversions);
        Assert.Equal(1, counts.Locations);
        Assert.Equal(2, counts.ProductGroups);
        Assert.Equal(3, counts.Recipes);
        Assert.Equal(2, counts.RecipePositions);
        Assert.Equal(1, counts.RecipeNestings);
        Assert.Equal(1, counts.Userfields);
        Assert.Equal(0, counts.ProductBarcodes);
    }

    [Fact]
    public void Manifest_Roundtrip_Preserves_Version_ExtractedAt_And_AllCollections()
    {
        var extractedAt = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

        var original = new GrocyManifest
        {
            ExtractedAt = extractedAt,
            Products =
            [
                new GrocyProduct(1, "Milk", 2, 3, 13, 18, null, null, null, 7, null, null, null, null, null, null, null, null, null),
                new GrocyProduct(2, "Cheese", null, null, 13, 13, null, null, 1, -1, null, null, null, null, null, null, null, null, null),
            ],
            QuantityUnits =
            [
                new GrocyQuantityUnit(13, "Gram", "Base mass unit", null),
                new GrocyQuantityUnit(18, "Kg", null, null),
            ],
            QuantityUnitConversions =
            [
                new GrocyQuantityUnitConversion(1, 18, 13, 1000m, null, null),
            ],
            Locations = [new GrocyLocation(1, "Fridge", null, 0, null)],
            ProductGroups = [new GrocyProductGroup(1, "Dairy", null, null)],
            Recipes = [new GrocyRecipe(10, "Cheese Omelette", "<p>Mix eggs and cheese.</p>", 2, null, "normal", null, null, null)],
            RecipePositions = [new GrocyRecipePosition(1, 10, 2, 50m, 13, null, null, null, null, null, null, null)],
            RecipeNestings = [],
            Userfields = [new GrocyUserfield(1, "recipes", "original_recipe", "Source", "link", 0, null)],
            ProductBarcodes = [new GrocyProductBarcode(1, 1, "012345678901", null, 1m, null, null)],
        };

        // Serialize using ExtractCommand's options.
        var json = JsonSerializer.Serialize(original, WriteOptions);

        // Deserialize using IndexModel's options.
        var restored = JsonSerializer.Deserialize<GrocyManifest>(json, ReadOptions);

        Assert.NotNull(restored);
        Assert.Equal("1.0", restored.Version);
        Assert.Equal(extractedAt, restored.ExtractedAt);
        Assert.Equal(2, restored.Products.Count);
        Assert.Equal("Milk", restored.Products[0].Name);
        Assert.Equal(2, restored.QuantityUnits.Count);
        Assert.Equal("Gram", restored.QuantityUnits[0].Name);
        Assert.Single(restored.QuantityUnitConversions);
        Assert.Equal(1000m, restored.QuantityUnitConversions[0].Factor);
        Assert.Single(restored.Locations);
        Assert.Single(restored.ProductGroups);
        Assert.Single(restored.Recipes);
        Assert.Equal("Cheese Omelette", restored.Recipes[0].Name);
        Assert.Equal("normal", restored.Recipes[0].Type);
        Assert.Single(restored.RecipePositions);
        Assert.Equal(50m, restored.RecipePositions[0].Amount);
        Assert.Empty(restored.RecipeNestings);
        Assert.Single(restored.Userfields);
        Assert.Single(restored.ProductBarcodes);
        Assert.Equal("012345678901", restored.ProductBarcodes[0].Barcode);
    }

    [Fact]
    public void ManifestCounts_From_Empty_Manifest_Returns_All_Zeros()
    {
        var manifest = new GrocyManifest { ExtractedAt = DateTimeOffset.UtcNow };
        var counts = ManifestCounts.From(manifest);

        Assert.Equal(0, counts.Products);
        Assert.Equal(0, counts.QuantityUnits);
        Assert.Equal(0, counts.QuantityUnitConversions);
        Assert.Equal(0, counts.Locations);
        Assert.Equal(0, counts.ProductGroups);
        Assert.Equal(0, counts.Recipes);
        Assert.Equal(0, counts.RecipePositions);
        Assert.Equal(0, counts.RecipeNestings);
        Assert.Equal(0, counts.Userfields);
        Assert.Equal(0, counts.ProductBarcodes);
    }

    [Fact]
    public void Manifest_Json_Uses_CamelCase_Property_Names()
    {
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(manifest, WriteOptions);

        // The manifest envelope fields are camelCase.
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"extractedAt\"", json);
        Assert.Contains("\"products\"", json);
        Assert.Contains("\"quantityUnits\"", json);
        Assert.Contains("\"quantityUnitConversions\"", json);
        Assert.Contains("\"recipes\"", json);
        Assert.Contains("\"recipePositions\"", json);
        Assert.Contains("\"recipeNestings\"", json);
        Assert.Contains("\"userfields\"", json);
        Assert.Contains("\"productBarcodes\"", json);
    }
}
