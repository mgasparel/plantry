using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="CategoryStager"/> — the Grocy product group staging algorithm
/// (plantry-zcw.9).
///
/// Tests cover:
/// - Known alias table matches (exact Grocy name → Plantry category)
/// - Spices collapse anomaly (NeedsReview flag)
/// - Explicit CreateNew groups (Prepared / Homemade)
/// - Fuzzy contains fallback against existing category names
/// - Ambiguous fuzzy matches flagged NeedsReview
/// - No-match fallback → CreateNew, NeedsReview
/// - Rows are returned in Grocy-id order
/// </summary>
public sealed class CategoryStagerTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static GrocyManifest ManifestWith(IEnumerable<GrocyProductGroup> groups) =>
        new() { ExtractedAt = DateTimeOffset.UtcNow, ProductGroups = groups.ToList() };

    private static GrocyProductGroup Group(int id, string name) =>
        new(id, name, null, null);

    // ──────────── Known alias table ───────────────────────────────────────

    [Theory]
    [InlineData("Fruit & Veg",       "Fruits and Vegetables", false)]
    [InlineData("Frozen Food",       "Frozen",                false)]
    [InlineData("Meat",              "Meat & Fish",           false)]
    [InlineData("Drinks",            "Drinks",                false)]
    [InlineData("Condiments",        "Condiments",            false)]
    [InlineData("Herbs",             "Herbs and Spices",      false)]
    public void KnownAlias_AssignsExpectedPlantryNameAndMatchExisting(
        string grocyName, string expectedPlantryName, bool expectNeedsReview)
    {
        var manifest = ManifestWith([Group(1, grocyName)]);
        var existing = new[] { "Fruits and Vegetables", "Frozen", "Meat & Fish",
                                "Drinks", "Condiments", "Herbs and Spices" };

        var rows = CategoryStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(expectedPlantryName, row.PlantryName);
        Assert.Equal(CategoryMappingAction.MatchExisting, row.Action);
        Assert.Equal(expectNeedsReview ? CategoryStagingStatus.NeedsReview : CategoryStagingStatus.Auto, row.Status);
    }

    [Theory]
    [InlineData("Spices")]
    [InlineData("Spice")]
    public void Spices_CollapsesToHerbsAndSpices_AndFlagsNeedsReview(string grocyName)
    {
        var manifest = ManifestWith([Group(1, grocyName)]);
        var existing = new[] { "Herbs and Spices" };

        var rows = CategoryStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("Herbs and Spices", row.PlantryName);
        Assert.Equal(CategoryMappingAction.MatchExisting, row.Action);
        Assert.Equal(CategoryStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("Herbs and Spices", row.AnomalyNote);
    }

    // ──────────── Explicit CreateNew ─────────────────────────────────────

    [Theory]
    [InlineData("Prepared (Homemade)")]
    [InlineData("Prepared")]
    [InlineData("Homemade")]
    public void ExplicitCreateNew_ReturnsCreateNewAction(string grocyName)
    {
        var manifest = ManifestWith([Group(5, grocyName)]);

        var rows = CategoryStager.Stage(manifest, []);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(CategoryMappingAction.CreateNew, row.Action);
        Assert.Equal(CategoryStagingStatus.Auto, row.Status);
        Assert.Equal(grocyName, row.PlantryName);
    }

    // ──────────── Fuzzy fallback ──────────────────────────────────────────

    [Fact]
    public void FuzzyFallback_ContainsMatch_ReturnsMatchExistingAuto()
    {
        // "Bakery" contains "Bake" in a different direction — test the contains both ways
        var manifest = ManifestWith([Group(10, "Baked Goods")]);
        var existing = new[] { "Baked Goods & Bread" };

        var rows = CategoryStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("Baked Goods & Bread", row.PlantryName);
        Assert.Equal(CategoryMappingAction.MatchExisting, row.Action);
        Assert.Equal(CategoryStagingStatus.Auto, row.Status);
        Assert.Null(row.AnomalyNote);
    }

    [Fact]
    public void FuzzyFallback_AmbiguousMatch_FlagsNeedsReview()
    {
        var manifest = ManifestWith([Group(3, "Dairy")]);
        var existing = new[] { "Dairy Products", "Dairy Alternatives" };

        var rows = CategoryStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(CategoryMappingAction.MatchExisting, row.Action);
        Assert.Equal(CategoryStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("Dairy Products", row.AnomalyNote);
        Assert.Contains("Dairy Alternatives", row.AnomalyNote);
    }

    [Fact]
    public void FuzzyFallback_NoMatch_ReturnsCreateNewNeedsReview()
    {
        var manifest = ManifestWith([Group(7, "Pet Food")]);
        var existing = new[] { "Fruits and Vegetables", "Frozen", "Meat & Fish" };

        var rows = CategoryStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("Pet Food", row.PlantryName);
        Assert.Equal(CategoryMappingAction.CreateNew, row.Action);
        Assert.Equal(CategoryStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
    }

    // ──────────── Ordering ───────────────────────────────────────────────

    [Fact]
    public void Stage_ReturnsRowsInGrocyIdOrder()
    {
        var manifest = ManifestWith(
        [
            Group(5, "Drinks"),
            Group(1, "Meat"),
            Group(3, "Herbs"),
        ]);

        var rows = CategoryStager.Stage(manifest, []);

        Assert.Equal([1, 3, 5], rows.Select(r => r.GrocyId).ToList());
    }

    // ──────────── Crosswalk helper ────────────────────────────────────────

    [Fact]
    public void CategoryCrosswalk_ResolvePath_UsesSameDirectoryAsManifest()
    {
        var manifestPath  = Path.Combine("C:", "Plantry", "grocy-manifest.json");
        var crosswalkPath = CategoryCrosswalk.ResolvePath(manifestPath);

        Assert.Equal(Path.Combine("C:", "Plantry", "category-crosswalk.json"), crosswalkPath);
    }

    [Fact]
    public async Task CategoryCrosswalk_WriteAndRead_RoundTrips()
    {
        var tempDir       = Path.GetTempPath();
        var crosswalkPath = Path.Combine(tempDir, $"category-crosswalk-test-{Guid.NewGuid()}.json");

        try
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var crosswalk = new CategoryCrosswalk
            {
                CommittedAt = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero),
                Mappings    = new Dictionary<string, Guid>
                {
                    ["1"] = id1,
                    ["2"] = id2,
                },
            };

            await crosswalk.WriteAsync(crosswalkPath);
            var restored = await CategoryCrosswalk.TryReadAsync(crosswalkPath);

            Assert.NotNull(restored);
            Assert.Equal("1.0", restored.Version);
            Assert.Equal(2, restored.Mappings.Count);
            Assert.Equal(id1, restored.Mappings["1"]);
            Assert.Equal(id2, restored.Mappings["2"]);
        }
        finally
        {
            if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
        }
    }
}
