using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="ImportSummaryService"/> count aggregation logic (plantry-zcw.8).
///
/// These tests exercise the pure static aggregation helpers — StageCategoryGroups,
/// StageLocations, and MatchesExistingName — and the TradeoffLog contents.
/// The <see cref="ImportSummaryService.ComputeAsync"/> method (which requires file I/O
/// and repository injection) is not tested here; it delegates to the same stagers
/// already covered by UnitStagerTests, ProductStagerTests, and RecipeStagerTests.
/// </summary>
public sealed class ImportSummaryServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GrocyManifest EmptyManifest() => new()
    {
        ExtractedAt = DateTimeOffset.UtcNow,
    };

    private static GrocyManifest ManifestWithGroups(params string[] names) => new()
    {
        ExtractedAt = DateTimeOffset.UtcNow,
        ProductGroups = names
            .Select((n, i) => new GrocyProductGroup(i + 1, n, null, null))
            .ToList(),
    };

    private static GrocyManifest ManifestWithLocations(params (string name, int isFreezer)[] locs) => new()
    {
        ExtractedAt = DateTimeOffset.UtcNow,
        Locations = locs
            .Select((l, i) => new GrocyLocation(i + 1, l.name, null, l.isFreezer, null))
            .ToList(),
    };

    /// <summary>
    /// The actual seeded Plantry category names from CatalogReferenceDataSeeder.
    /// </summary>
    private static readonly IReadOnlySet<string> RealCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Dairy & Eggs", "Meat & Fish", "Fruits and Vegetables", "Bread & Bakery",
        "Deli", "Frozen", "Pantry Staples", "Canned & Jarred", "Drinks",
        "Condiments", "Herbs and Spices", "Snacks", "Other",
    };

    /// <summary>
    /// The actual seeded Plantry location names from CatalogReferenceDataSeeder.
    /// </summary>
    private static readonly IReadOnlySet<string> RealLocationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Fridge", "Freezer", "Pantry", "Counter",
    };

    // ── Category staging tests ────────────────────────────────────────────────

    [Fact]
    public void StageCategoryGroups_EmptyManifest_ReturnsAllZeros()
    {
        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(EmptyManifest(), RealCategoryNames);

        Assert.Equal(0, matched);
        Assert.Equal(0, created);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Dairy & Eggs")]
    [InlineData("DAIRY & EGGS")]          // case-insensitive
    [InlineData("Meat & Fish")]
    [InlineData("Fruits and Vegetables")]
    [InlineData("Bread & Bakery")]
    [InlineData("Deli")]
    [InlineData("Frozen")]
    [InlineData("Pantry Staples")]
    [InlineData("Canned & Jarred")]
    [InlineData("Drinks")]
    [InlineData("Condiments")]
    [InlineData("Herbs and Spices")]
    [InlineData("Snacks")]
    [InlineData("Other")]
    public void StageCategoryGroups_ExactSeededName_CountsAsMatched(string name)
    {
        var manifest = ManifestWithGroups(name);

        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(manifest, RealCategoryNames);

        Assert.Equal(1, matched);
        Assert.Equal(0, created);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Herbs")]          // partial match → "Herbs and Spices" contains "Herbs"
    [InlineData("Spices")]         // partial match → "Herbs and Spices" contains "Spices"
    [InlineData("Dairy")]          // partial match → "Dairy & Eggs" contains "Dairy"
    [InlineData("Meat")]           // partial match → "Meat & Fish" contains "Meat"
    [InlineData("Fruits")]         // partial match → "Fruits and Vegetables" contains "Fruits"
    [InlineData("Bakery")]         // partial match → "Bread & Bakery" contains "Bakery"
    public void StageCategoryGroups_PartialMatchName_CountsAsMatched(string name)
    {
        var manifest = ManifestWithGroups(name);

        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(manifest, RealCategoryNames);

        Assert.Equal(1, matched);
        Assert.Equal(0, created);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Prepared (Homemade)")]
    [InlineData("Garden")]
    [InlineData("International")]
    [InlineData("Pet Food")]
    public void StageCategoryGroups_UnknownName_CountsAsCreated(string name)
    {
        var manifest = ManifestWithGroups(name);

        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(manifest, RealCategoryNames);

        Assert.Equal(0, matched);
        Assert.Equal(1, created);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void StageCategoryGroups_MixedGroups_CountsCorrectly()
    {
        // "Dairy & Eggs" → matched, "Garden" → created, "Meat & Fish" → matched, "Prepared (Homemade)" → created
        var manifest = ManifestWithGroups("Dairy & Eggs", "Garden", "Meat & Fish", "Prepared (Homemade)");

        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(manifest, RealCategoryNames);

        Assert.Equal(2, matched);
        Assert.Equal(2, created);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void StageCategoryGroups_EmptyExistingNames_AllCountAsCreated()
    {
        var manifest = ManifestWithGroups("Dairy & Eggs", "Meat & Fish");
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (int matched, int created, int skipped) = ImportSummaryService.StageCategoryGroups(manifest, emptySet);

        Assert.Equal(0, matched);
        Assert.Equal(2, created);
        Assert.Equal(0, skipped);
    }

    // ── Location staging tests ─────────────────────────────────────────────────

    [Fact]
    public void StageLocations_EmptyManifest_ReturnsAllZeros()
    {
        (int matched, int created, int skipped) = ImportSummaryService.StageLocations(EmptyManifest(), RealLocationNames);

        Assert.Equal(0, matched);
        Assert.Equal(0, created);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Fridge")]
    [InlineData("fridge")]         // case-insensitive
    [InlineData("Freezer")]
    [InlineData("Pantry")]
    [InlineData("Counter")]
    public void StageLocations_KnownSeededName_CountsAsMatched(string name)
    {
        var manifest = ManifestWithLocations((name, 0));

        (int matched, int created, int skipped) = ImportSummaryService.StageLocations(manifest, RealLocationNames);

        Assert.Equal(1, matched);
        Assert.Equal(0, created);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData("Garden")]
    [InlineData("Attic")]
    [InlineData("Basement Shelves")]
    [InlineData("Outside Store")]
    public void StageLocations_UnknownName_CountsAsCreated(string name)
    {
        var manifest = ManifestWithLocations((name, 0));

        (int matched, int created, int skipped) = ImportSummaryService.StageLocations(manifest, RealLocationNames);

        Assert.Equal(0, matched);
        Assert.Equal(1, created);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void StageLocations_MixedLocations_CountsCorrectly()
    {
        // "Fridge" → matched, "Garden" → created, "Freezer" → matched, "Attic" → created
        var manifest = ManifestWithLocations(
            ("Fridge", 0), ("Garden", 0), ("Freezer", 1), ("Attic", 0));

        (int matched, int created, int skipped) = ImportSummaryService.StageLocations(manifest, RealLocationNames);

        Assert.Equal(2, matched);
        Assert.Equal(2, created);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void StageLocations_EmptyExistingNames_AllCountAsCreated()
    {
        var manifest = ManifestWithLocations(("Fridge", 0), ("Freezer", 1));
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (int matched, int created, int skipped) = ImportSummaryService.StageLocations(manifest, emptySet);

        Assert.Equal(0, matched);
        Assert.Equal(2, created);
        Assert.Equal(0, skipped);
    }

    // ── MatchesExistingName tests ─────────────────────────────────────────────

    [Fact]
    public void MatchesExistingName_ExactMatch_ReturnsTrue()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dairy & Eggs", "Produce", "Other" };

        Assert.True(ImportSummaryService.MatchesExistingName("Dairy & Eggs", names));
        Assert.True(ImportSummaryService.MatchesExistingName("Other", names));
    }

    [Fact]
    public void MatchesExistingName_CaseInsensitive_ReturnsTrue()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dairy & Eggs" };

        Assert.True(ImportSummaryService.MatchesExistingName("DAIRY & EGGS", names));
        Assert.True(ImportSummaryService.MatchesExistingName("dairy & eggs", names));
    }

    [Fact]
    public void MatchesExistingName_PartialContains_ReturnsTrue()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Herbs and Spices" };

        // "Herbs" is contained in "Herbs and Spices"
        Assert.True(ImportSummaryService.MatchesExistingName("Herbs", names));
        // "Spices" is contained in "Herbs and Spices"
        Assert.True(ImportSummaryService.MatchesExistingName("Spices", names));
    }

    [Fact]
    public void MatchesExistingName_NoMatch_ReturnsFalse()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dairy & Eggs", "Meat & Fish" };

        Assert.False(ImportSummaryService.MatchesExistingName("Garden", names));
        Assert.False(ImportSummaryService.MatchesExistingName("Prepared (Homemade)", names));
    }

    [Fact]
    public void MatchesExistingName_EmptySet_ReturnsFalse()
    {
        var emptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ImportSummaryService.MatchesExistingName("Dairy", emptyNames));
    }

    // ── TradeoffLog tests ─────────────────────────────────────────────────────

    [Fact]
    public void TradeoffLog_All_HasExactly15Entries()
    {
        Assert.Equal(15, TradeoffLog.All.Count);
    }

    [Fact]
    public void TradeoffLog_All_IdsAreT1ThroughT15()
    {
        var ids = TradeoffLog.All.Select(e => e.Id).ToList();

        for (int i = 1; i <= 15; i++)
            Assert.Contains($"T{i}", ids);
    }

    [Fact]
    public void TradeoffLog_All_EachEntryHasNonEmptyTitleAndDescription()
    {
        foreach (var entry in TradeoffLog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id),
                $"Entry {entry.Id} has empty Id");
            Assert.False(string.IsNullOrWhiteSpace(entry.Title),
                $"Entry {entry.Id} has empty Title");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description),
                $"Entry {entry.Id} has empty Description");
        }
    }

    [Fact]
    public void TradeoffLog_T1_CoversSingleUnitRoleCollapse()
    {
        var t1 = TradeoffLog.All.Single(e => e.Id == "T1");
        Assert.Contains("qu_id_stock", t1.Description);
    }

    [Fact]
    public void TradeoffLog_T8_CoversAnomaly()
    {
        var t8 = TradeoffLog.All.Single(e => e.Id == "T8");
        Assert.Contains("tsp", t8.Description);
        Assert.Contains("tbsp", t8.Description);
    }

    [Fact]
    public void TradeoffLog_T13_CoversNestingFlattening()
    {
        var t13 = TradeoffLog.All.Single(e => e.Id == "T13");
        Assert.Contains("flattened", t13.Description.ToLowerInvariant());
    }
}
