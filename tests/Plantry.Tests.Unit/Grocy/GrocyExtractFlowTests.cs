using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Plantry.Migration.Grocy;
using Plantry.Tests.Unit.Grocy.Fixtures;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// End-to-end flow tests for <see cref="GrocyClient"/> + <see cref="ExtractCommand"/>
/// using a stub HTTP handler and canned snake_case JSON fixtures.
///
/// Coverage target (per issue plantry-nax):
///   1. GrocyClient deserializes Grocy's snake_case wire JSON into DTOs over a real HTTP boundary.
///   2. ExtractCommand type-filter: only recipes with type == "normal" survive.
///   3. ExtractCommand orphan-filter: positions and nestings belonging to non-normal recipe ids are dropped.
///   4. Written manifest round-trips and ManifestCounts.From reflects filtered counts.
///
/// NOT covered here (already in GrocyManifestTests):
///   - ManifestCounts.From count math
///   - Manifest serialize/deserialize round-trip and camelCase property names
/// </summary>
public sealed class GrocyExtractFlowTests : IDisposable
{
    // ── fixture data expected counts ──────────────────────────────────────────────────
    //
    // Fixture has 5 recipes total:
    //   id=10 type="normal"         (Scrambled Eggs)
    //   id=11 type="normal"         (Butter Cake)
    //   id=20 type="mealplan-day"   → filtered out
    //   id=21 type="mealplan-shadow" → filtered out
    //   id=22 type="mealplan-week"  → filtered out
    //
    // RecipePositions (5 total):
    //   id=101 recipe_id=10  → kept   (normal)
    //   id=102 recipe_id=10  → kept   (normal)
    //   id=103 recipe_id=11  → kept   (normal)
    //   id=201 recipe_id=20  → dropped (mealplan-day)
    //   id=202 recipe_id=21  → dropped (mealplan-shadow)
    //
    // RecipeNestings (3 total):
    //   id=301 recipe_id=11  → kept   (normal)
    //   id=302 recipe_id=20  → dropped (mealplan-day)
    //   id=303 recipe_id=22  → dropped (mealplan-week)
    //
    // Other counts (all pass through unfiltered):
    //   Products:              3
    //   QuantityUnits:         3
    //   QuantityUnitConversions: 2
    //   Locations:             2
    //   ProductGroups:         2
    //   Userfields:            1
    //   ProductBarcodes:       1

    private const int ExpectedNormalRecipes = 2;
    private const int ExpectedPositions = 3;
    private const int ExpectedNestings = 1;

    private readonly string _tempManifestPath;

    public GrocyExtractFlowTests()
    {
        _tempManifestPath = Path.Combine(Path.GetTempPath(), $"grocy-test-manifest-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempManifestPath))
            File.Delete(_tempManifestPath);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────

    private static string FixtureDir =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Grocy", "Fixtures");

    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(FixtureDir, fileName));

    private (GrocyClient Client, ExtractCommand Command) BuildSubject()
    {
        var stub = new StubHttpMessageHandler()
            .OnPath("api/objects/products",                  LoadFixture("products.json"))
            .OnPath("api/objects/quantity_units",            LoadFixture("quantity_units.json"))
            .OnPath("api/objects/quantity_unit_conversions", LoadFixture("quantity_unit_conversions.json"))
            .OnPath("api/objects/locations",                 LoadFixture("locations.json"))
            .OnPath("api/objects/product_groups",            LoadFixture("product_groups.json"))
            .OnPath("api/objects/recipes",                   LoadFixture("recipes.json"))
            .OnPath("api/objects/recipes_pos",               LoadFixture("recipes_pos.json"))
            .OnPath("api/objects/recipes_nestings",          LoadFixture("recipes_nestings.json"))
            .OnPath("api/objects/userfields",                LoadFixture("userfields.json"))
            .OnPath("api/objects/product_barcodes",         LoadFixture("product_barcodes.json"))
            // Per-recipe userfield endpoint — return empty (no original_recipe URLs in this fixture set).
            .OnPath("api/userfields/recipes", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });

        var http = new HttpClient(stub)
        {
            BaseAddress = new Uri("https://stub.grocy.invalid/")
        };

        var options = Options.Create(new GrocyOptions
        {
            Url       = "https://stub.grocy.invalid",
            ApiKey    = "test-key",
            ManifestPath = _tempManifestPath,
        });

        var client  = new GrocyClient(http, options);
        var command = new ExtractCommand(client, options);

        return (client, command);
    }

    // ── tests ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Recipes_OnlyNormal_TypesKept()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        // All five recipes come over the wire; only the two with type="normal" should survive.
        Assert.Equal(ExpectedNormalRecipes, manifest.Recipes.Count);
        Assert.All(manifest.Recipes, r =>
            Assert.Equal("normal", r.Type, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_Recipes_NonNormal_Ids_Absent()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        var ids = manifest.Recipes.Select(r => r.Id).ToHashSet();
        Assert.Contains(10, ids);
        Assert.Contains(11, ids);
        // Non-normal recipe ids must not appear.
        Assert.DoesNotContain(20, ids);
        Assert.DoesNotContain(21, ids);
        Assert.DoesNotContain(22, ids);
    }

    [Fact]
    public async Task ExecuteAsync_RecipePositions_OrphanDropped()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        // Positions 201 (recipe_id=20) and 202 (recipe_id=21) are orphans — must be dropped.
        Assert.Equal(ExpectedPositions, manifest.RecipePositions.Count);
        var positionIds = manifest.RecipePositions.Select(p => p.Id).ToHashSet();
        Assert.Contains(101, positionIds);
        Assert.Contains(102, positionIds);
        Assert.Contains(103, positionIds);
        Assert.DoesNotContain(201, positionIds);
        Assert.DoesNotContain(202, positionIds);
    }

    [Fact]
    public async Task ExecuteAsync_RecipeNestings_OrphanDropped()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        // Nesting 301 (recipe_id=11 normal) → kept.
        // Nesting 302 (recipe_id=20 mealplan-day) → dropped.
        // Nesting 303 (recipe_id=22 mealplan-week) → dropped.
        // ExpectedNestings == 1; Assert.Single gives a better error message for count=1.
        var nesting = Assert.Single(manifest.RecipeNestings);
        Assert.Equal(301, nesting.Id);
        // Verify orphan ids are absent.
        Assert.DoesNotContain(manifest.RecipeNestings, n => n.Id == 302);
        Assert.DoesNotContain(manifest.RecipeNestings, n => n.Id == 303);
    }

    [Fact]
    public async Task ExecuteAsync_SnakeCase_Products_Deserialized_Correctly()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        Assert.Equal(3, manifest.Products.Count);

        // Spot-check: id, name, and a numeric (qu_id_stock) from the first product.
        var flour = manifest.Products.Single(p => p.Id == 1);
        Assert.Equal("Flour", flour.Name);
        Assert.Equal(3, flour.QuIdStock);        // qu_id_stock snake_case field
        Assert.Equal(1, flour.ProductGroupId);   // product_group_id
        Assert.Equal(2, flour.LocationId);       // location_id
    }

    [Fact]
    public async Task ExecuteAsync_SnakeCase_QuantityUnits_Deserialized_Correctly()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        Assert.Equal(3, manifest.QuantityUnits.Count);
        var gram = manifest.QuantityUnits.Single(u => u.Id == 3);
        Assert.Equal("Gram", gram.Name);
        Assert.Equal("Base mass unit", gram.Description);
    }

    [Fact]
    public async Task ExecuteAsync_SnakeCase_QuantityUnitConversions_Deserialized_Correctly()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        Assert.Equal(2, manifest.QuantityUnitConversions.Count);
        var kgToGram = manifest.QuantityUnitConversions.Single(c => c.Id == 1);
        Assert.Equal(5, kgToGram.FromQuId);   // from_qu_id
        Assert.Equal(3, kgToGram.ToQuId);     // to_qu_id
        Assert.Equal(1000m, kgToGram.Factor); // factor
    }

    [Fact]
    public async Task ExecuteAsync_SnakeCase_RecipePositions_Amount_Deserialized_Correctly()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        var pos101 = manifest.RecipePositions.Single(p => p.Id == 101);
        Assert.Equal(10, pos101.RecipeId);  // recipe_id
        Assert.Equal(2,  pos101.ProductId); // product_id
        Assert.Equal(3m, pos101.Amount);    // amount
        Assert.Equal(4,  pos101.QuId);      // qu_id
    }

    [Fact]
    public async Task ExecuteAsync_SnakeCase_RecipeNestings_Deserialized_Correctly()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        var nesting = manifest.RecipeNestings.Single(n => n.Id == 301);
        Assert.Equal(11, nesting.RecipeId);          // recipe_id
        Assert.Equal(10, nesting.IncludesRecipeId);  // includes_recipe_id
        Assert.Equal(1m, nesting.Servings);          // servings
    }

    [Fact]
    public async Task ExecuteAsync_WritesManifest_RoundTrips_With_FilteredCounts()
    {
        var (_, command) = BuildSubject();

        var (returnedManifest, filePath) = await command.ExecuteAsync();

        // File must have been written.
        Assert.True(File.Exists(filePath), $"Manifest file not found at {filePath}");

        // Re-read using the same options IndexModel uses (case-insensitive).
        var readOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await using var stream = File.OpenRead(filePath);
        var restored = await JsonSerializer.DeserializeAsync<GrocyManifest>(stream, readOptions);

        Assert.NotNull(restored);

        // ManifestCounts.From on the restored manifest must match the filtered fixture counts.
        var counts = ManifestCounts.From(restored);
        Assert.Equal(ExpectedNormalRecipes, counts.Recipes);
        Assert.Equal(ExpectedPositions,     counts.RecipePositions);
        Assert.Equal(ExpectedNestings,      counts.RecipeNestings);

        // The returned manifest and the written+re-read manifest must agree on recipe count.
        Assert.Equal(returnedManifest.Recipes.Count, restored.Recipes.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ManifestCounts_Matches_FilteredFixtureCounts()
    {
        var (_, command) = BuildSubject();

        var (manifest, _) = await command.ExecuteAsync();

        var counts = ManifestCounts.From(manifest);

        Assert.Equal(3,                    counts.Products);
        Assert.Equal(3,                    counts.QuantityUnits);
        Assert.Equal(2,                    counts.QuantityUnitConversions);
        Assert.Equal(2,                    counts.Locations);
        Assert.Equal(2,                    counts.ProductGroups);
        Assert.Equal(ExpectedNormalRecipes, counts.Recipes);
        Assert.Equal(ExpectedPositions,     counts.RecipePositions);
        Assert.Equal(ExpectedNestings,      counts.RecipeNestings);
        Assert.Equal(1,                    counts.Userfields);
        Assert.Equal(1,                    counts.ProductBarcodes);
    }
}
