using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="UnitStager"/> — the dimension assignment algorithm for
/// Grocy unit staging (plantry-zcw.2).
///
/// Tests cover:
/// - Seed-match by name/synonym (Gram→g, Kg→kg, Liter→l, etc.)
/// - tsp/tbsp anomaly detection (Grocy stored anomalous factors)
/// - Cup 237-vs-240 drift flagging
/// - 1/2 Cup and 1/4 Cup → Skipped
/// - Known create-new units (Pint, Quart, Case12, Case24)
/// - Graph-based dimension assignment (connected component → mass/volume/count)
/// - Isolated units → count, factor 1
/// - factor_to_base derivation
/// - All 25 Grocy units (per the acceptance criterion)
/// </summary>
public sealed class UnitStagerTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static GrocyManifest ManifestWith(
        IEnumerable<GrocyQuantityUnit> units,
        IEnumerable<GrocyQuantityUnitConversion>? conversions = null)
    {
        return new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits = units.ToList(),
            QuantityUnitConversions = conversions?.ToList() ?? [],
        };
    }

    private static GrocyQuantityUnit Unit(int id, string name) =>
        new(id, name, null, null);

    private static GrocyQuantityUnitConversion Conv(int id, int from, int to, decimal factor, int? productId = null) =>
        new(id, from, to, factor, productId, null);

    // ──────────── Seed-match tests ─────────────────────────────────────────

    [Theory]
    [InlineData("Gram", "g", "mass", 1.0)]
    [InlineData("gram", "g", "mass", 1.0)]
    [InlineData("Kg", "kg", "mass", 1000.0)]
    [InlineData("Kilogram", "kg", "mass", 1000.0)]
    [InlineData("oz", "oz", "mass", 28.3495)]
    [InlineData("Ounce", "oz", "mass", 28.3495)]
    [InlineData("ml", "ml", "volume", 1.0)]
    [InlineData("Milliliter", "ml", "volume", 1.0)]
    [InlineData("Liter", "l", "volume", 1000.0)]
    [InlineData("l", "l", "volume", 1000.0)]
    [InlineData("Piece", "ea", "count", 1.0)]
    [InlineData("ea", "ea", "count", 1.0)]
    [InlineData("Pack", "pk", "count", 1.0)]
    [InlineData("pk", "pk", "count", 1.0)]
    public void SeedMatch_AssignsCorrectDimensionCodeAndFactor(
        string grocyName, string expectedCode, string expectedDimension, double expectedFactor)
    {
        var manifest = ManifestWith([Unit(1, grocyName)]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(expectedCode, row.PlantryCode);
        Assert.Equal(expectedDimension, row.Dimension);
        Assert.Equal((decimal)expectedFactor, row.FactorToBase);
        Assert.Equal(UnitMappingAction.MatchExisting, row.Action);
        Assert.NotEqual(UnitStagingStatus.Skipped, row.Status);
    }

    [Fact]
    public void Cup_IsSeedMatched_WithPlantryFactor240_AndDriftAnomalyFlagged()
    {
        var manifest = ManifestWith([Unit(9, "Cup")]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("cup", row.PlantryCode);
        Assert.Equal("volume", row.Dimension);
        Assert.Equal(240m, row.FactorToBase);
        Assert.Equal(UnitMappingAction.MatchExisting, row.Action);
        Assert.Equal(UnitStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("237", row.AnomalyNote);
    }

    [Fact]
    public void Tsp_IsSeedMatched_WithPlantryFactor_AndAnomalyFlagged()
    {
        var manifest = ManifestWith([Unit(10, "tsp")]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("tsp", row.PlantryCode);
        Assert.Equal("volume", row.Dimension);
        Assert.Equal(4.92892m, row.FactorToBase);
        Assert.Equal(UnitStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("ANOMALY", row.AnomalyNote);
        Assert.Contains("14.7867", row.AnomalyNote);
    }

    [Fact]
    public void Tbsp_IsSeedMatched_WithPlantryFactor_AndAnomalyFlagged()
    {
        var manifest = ManifestWith([Unit(11, "tbsp")]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("tbsp", row.PlantryCode);
        Assert.Equal("volume", row.Dimension);
        Assert.Equal(14.7868m, row.FactorToBase);
        Assert.Equal(UnitStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("ANOMALY", row.AnomalyNote);
        Assert.Contains("17.7581", row.AnomalyNote);
    }

    // ──────────── Skip tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("1/2 Cup")]
    [InlineData("1/4 Cup")]
    public void FractionCups_AreSkipped(string grocyName)
    {
        var manifest = ManifestWith([Unit(26, grocyName)]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(UnitStagingStatus.Skipped, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("fraction", row.AnomalyNote, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────── Known create-new tests ─────────────────────────────────

    [Theory]
    [InlineData("Pint", "pt", "volume", 474.0)]
    [InlineData("Quart", "qt", "volume", 948.0)]
    [InlineData("Case12", "case12", "count", 12.0)]
    [InlineData("Case24", "case24", "count", 24.0)]
    public void KnownCreateNew_AssignsCorrectCodeDimensionAndFactor(
        string grocyName, string expectedCode, string expectedDimension, double expectedFactor)
    {
        var manifest = ManifestWith([Unit(1, grocyName)]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(expectedCode, row.PlantryCode);
        Assert.Equal(expectedDimension, row.Dimension);
        Assert.Equal((decimal)expectedFactor, row.FactorToBase);
        Assert.Equal(UnitMappingAction.CreateNew, row.Action);
        Assert.Equal(UnitStagingStatus.Auto, row.Status);
        Assert.Null(row.AnomalyNote);
    }

    // ──────────── Graph-based dimension tests ────────────────────────────

    [Fact]
    public void GraphBased_ConnectedToGram_IsMass()
    {
        // Bottle (id=8) connected to Gram (id=13) via a product-specific conversion
        // but we only use global conversions (productId = null) for graph assignment.
        // This test verifies that a unit in the same global-conversion component as Gram
        // is assigned dimension=mass.
        //
        // Setup: UnknownUnit(99) ↔ Gram(13) with global factor 500.
        // UnknownUnit is 1/500 of a gram → factor_to_base = 0.002 (for mass, base = g = 1)
        // Actually: 1 UnknownUnit = 500 Gram → factor_to_base(UnknownUnit) = 500

        var manifest = ManifestWith(
            [Unit(13, "Gram"), Unit(99, "UnknownMassUnit")],
            [Conv(1, 99, 13, 500m)]);  // 1 UnknownMassUnit = 500 Gram

        var rows = UnitStager.Stage(manifest);
        Assert.Equal(2, rows.Count);

        // Gram → seed match
        var gram = rows.First(r => r.GrocyId == 13);
        Assert.Equal("g", gram.PlantryCode);
        Assert.Equal("mass", gram.Dimension);
        Assert.Equal(1m, gram.FactorToBase);

        // UnknownMassUnit → graph-resolved to mass, factor 500
        var unknown = rows.First(r => r.GrocyId == 99);
        Assert.Equal("mass", unknown.Dimension);
        Assert.Equal(500m, unknown.FactorToBase);
        Assert.Equal(UnitMappingAction.CreateNew, unknown.Action);
    }

    [Fact]
    public void GraphBased_ConnectedToMl_IsVolume()
    {
        // A unit connected to ml via global conversions gets dimension=volume
        var manifest = ManifestWith(
            [Unit(15, "ml"), Unit(88, "UnknownVolumeUnit")],
            [Conv(1, 88, 15, 250m)]);  // 1 UnknownVolumeUnit = 250 ml

        var rows = UnitStager.Stage(manifest);

        var unknown = rows.First(r => r.GrocyId == 88);
        Assert.Equal("volume", unknown.Dimension);
        Assert.Equal(250m, unknown.FactorToBase);
    }

    [Fact]
    public void GraphBased_ProductSpecificConversions_IgnoredForDimensionAssignment()
    {
        // Product-specific conversions (productId != null) should NOT be used to build
        // the global conversion graph. A unit with only product-specific conversions
        // should be treated as isolated → count, factor 1.

        var manifest = ManifestWith(
            [Unit(1, "Can"), Unit(13, "Gram")],
            [Conv(1, 1, 13, 400m, productId: 42)]);  // product-specific, ignored

        var rows = UnitStager.Stage(manifest);

        var can = rows.First(r => r.GrocyId == 1);
        // "Can" is not in any seed/synonym table and has only a product-specific conversion
        // → should fall through to count, factor 1
        Assert.Equal("count", can.Dimension);
        Assert.Equal(1m, can.FactorToBase);
        Assert.Equal(UnitMappingAction.CreateNew, can.Action);
    }

    [Fact]
    public void IsolatedUnit_NoGlobalConversions_IsCountFactor1()
    {
        var manifest = ManifestWith([Unit(4, "Can")]);

        var rows = UnitStager.Stage(manifest);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("count", row.Dimension);
        Assert.Equal(1m, row.FactorToBase);
        Assert.Equal(UnitMappingAction.CreateNew, row.Action);
        Assert.Equal(UnitStagingStatus.Auto, row.Status);
    }

    // ──────────── Full 25-unit acceptance test ────────────────────────────

    [Fact]
    public void Stage_AllTwentyFiveGrocyUnits_ProducesExpectedRows()
    {
        // The 25 Grocy units from the live instance (per grocy-import-plan.md §3 + §4.1).
        // Global conversions extracted from the manifest (22 edges, product_id = null).
        // This mirrors what a real manifest would contain.

        var units = new List<GrocyQuantityUnit>
        {
            Unit(2, "Piece"),
            Unit(3, "Pack"),
            Unit(4, "Can"),
            Unit(5, "Head"),
            Unit(6, "Case12"),
            Unit(7, "Jar"),
            Unit(8, "Bottle"),
            Unit(9, "Cup"),
            Unit(10, "tsp"),
            Unit(11, "tbsp"),
            Unit(12, "oz"),
            Unit(13, "Gram"),
            Unit(14, "Clove"),
            Unit(15, "ml"),
            Unit(17, "Liter"),
            Unit(18, "Kg"),
            Unit(20, "Portion"),
            Unit(21, "Case24"),
            Unit(22, "Bulb"),
            Unit(23, "Bunch"),
            Unit(24, "Pint"),
            Unit(25, "Quart"),
            Unit(26, "1/2 Cup"),
            Unit(27, "1/4 Cup"),
            Unit(28, "Recipe"),
        };

        // Representative global conversions (mass + volume chains; no product-specific).
        // In a real manifest: Kg↔Gram, Liter↔ml, Cup↔ml, tsp↔ml, tbsp↔ml, oz↔Gram, Pint↔Cup, Quart↔Pint.
        // For staging we only need enough to resolve dimensions and factors.
        var conversions = new List<GrocyQuantityUnitConversion>
        {
            Conv(1,  18, 13, 1000m),     // Kg → Gram (1 Kg = 1000 Gram)
            Conv(2,  13, 18, 0.001m),    // Gram → Kg
            Conv(3,  17, 15, 1000m),     // Liter → ml
            Conv(4,  15, 17, 0.001m),    // ml → Liter
            Conv(5,   9, 15, 237m),      // Cup → ml (Grocy's stored factor, anomalous)
            Conv(6,  10, 15, 14.7867m),  // tsp → ml (Grocy's stored factor, anomalous)
            Conv(7,  11, 15, 17.7581m),  // tbsp → ml (Grocy's stored factor, anomalous)
            Conv(8,  12, 13, 28.35m),    // oz → Gram
            Conv(9,  24,  9, 2m),        // Pint → Cup (1 Pint = 2 Cups in Grocy)
            Conv(10, 25, 24, 2m),        // Quart → Pint
            Conv(11, 26,  9, 0.5m),      // 1/2 Cup → Cup
            Conv(12, 27,  9, 0.25m),     // 1/4 Cup → Cup
        };

        var manifest = ManifestWith(units, conversions);

        var rows = UnitStager.Stage(manifest);

        Assert.Equal(25, rows.Count);

        // Verify GrocyId order
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 20, 21, 22, 23, 24, 25, 26, 27, 28],
            rows.Select(r => r.GrocyId).ToList());

        // ── Verify key rows ───────────────────────────────────────────────

        // Seed-matched mass
        AssertRow(rows, grocyId: 13, code: "g",  dim: "mass",   factor: 1m,      action: UnitMappingAction.MatchExisting, hasAnomaly: false);
        AssertRow(rows, grocyId: 18, code: "kg", dim: "mass",   factor: 1000m,   action: UnitMappingAction.MatchExisting, hasAnomaly: false);
        AssertRow(rows, grocyId: 12, code: "oz", dim: "mass",   factor: 28.3495m,action: UnitMappingAction.MatchExisting, hasAnomaly: false);

        // Seed-matched volume
        AssertRow(rows, grocyId: 15, code: "ml",   dim: "volume", factor: 1m,      action: UnitMappingAction.MatchExisting, hasAnomaly: false);
        AssertRow(rows, grocyId: 17, code: "l",    dim: "volume", factor: 1000m,   action: UnitMappingAction.MatchExisting, hasAnomaly: false);

        // Anomalous: Cup (drift), tsp (factor ≈ tbsp), tbsp (off 20%)
        var cup = rows.First(r => r.GrocyId == 9);
        Assert.Equal("cup", cup.PlantryCode);
        Assert.Equal(240m, cup.FactorToBase);
        Assert.Equal(UnitStagingStatus.NeedsReview, cup.Status);
        Assert.NotNull(cup.AnomalyNote);

        var tsp = rows.First(r => r.GrocyId == 10);
        Assert.Equal("tsp", tsp.PlantryCode);
        Assert.Equal(4.92892m, tsp.FactorToBase);
        Assert.Equal(UnitStagingStatus.NeedsReview, tsp.Status);
        Assert.NotNull(tsp.AnomalyNote);

        var tbsp = rows.First(r => r.GrocyId == 11);
        Assert.Equal("tbsp", tbsp.PlantryCode);
        Assert.Equal(14.7868m, tbsp.FactorToBase);
        Assert.Equal(UnitStagingStatus.NeedsReview, tbsp.Status);
        Assert.NotNull(tbsp.AnomalyNote);

        // Seed-matched count
        AssertRow(rows, grocyId: 2, code: "ea", dim: "count", factor: 1m, action: UnitMappingAction.MatchExisting, hasAnomaly: false);
        AssertRow(rows, grocyId: 3, code: "pk", dim: "count", factor: 1m, action: UnitMappingAction.MatchExisting, hasAnomaly: false);

        // Create-new volume
        AssertRow(rows, grocyId: 24, code: "pt", dim: "volume", factor: 474m, action: UnitMappingAction.CreateNew, hasAnomaly: false);
        AssertRow(rows, grocyId: 25, code: "qt", dim: "volume", factor: 948m, action: UnitMappingAction.CreateNew, hasAnomaly: false);

        // Create-new count
        AssertRow(rows, grocyId: 6, code: "case12", dim: "count", factor: 12m, action: UnitMappingAction.CreateNew, hasAnomaly: false);
        AssertRow(rows, grocyId: 21, code: "case24", dim: "count", factor: 24m, action: UnitMappingAction.CreateNew, hasAnomaly: false);

        // Skipped fractions
        var halfCup = rows.First(r => r.GrocyId == 26);
        Assert.Equal(UnitStagingStatus.Skipped, halfCup.Status);

        var quarterCup = rows.First(r => r.GrocyId == 27);
        Assert.Equal(UnitStagingStatus.Skipped, quarterCup.Status);

        // Discrete count units (isolated — no global conversions)
        var canIds = new[] { 4, 5, 7, 8, 14, 20, 22, 23, 28 };
        foreach (var id in canIds)
        {
            var r = rows.First(x => x.GrocyId == id);
            Assert.Equal("count", r.Dimension);
            Assert.Equal(1m, r.FactorToBase);
            Assert.Equal(UnitMappingAction.CreateNew, r.Action);
        }
    }

    // ──────────── Crosswalk path helper ──────────────────────────────────

    [Fact]
    public void UnitCrosswalk_ResolvePath_UsesSameDirectoryAsManifest()
    {
        var manifestPath = Path.Combine("C:", "Plantry", "grocy-manifest.json");
        var crosswalkPath = UnitCrosswalk.ResolvePath(manifestPath);

        Assert.Equal(Path.Combine("C:", "Plantry", "unit-crosswalk.json"), crosswalkPath);
    }

    [Fact]
    public async Task UnitCrosswalk_WriteAndRead_RoundTrips()
    {
        var tempDir = Path.GetTempPath();
        var crosswalkPath = Path.Combine(tempDir, $"unit-crosswalk-test-{Guid.NewGuid()}.json");

        try
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var crosswalk = new UnitCrosswalk
            {
                CommittedAt = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero),
                Mappings    = new Dictionary<string, Guid>
                {
                    ["13"] = id1,
                    ["18"] = id2,
                },
            };

            await crosswalk.WriteAsync(crosswalkPath);

            var restored = await UnitCrosswalk.TryReadAsync(crosswalkPath);

            Assert.NotNull(restored);
            Assert.Equal("1.0", restored.Version);
            Assert.Equal(2, restored.Mappings.Count);
            Assert.Equal(id1, restored.Mappings["13"]);
            Assert.Equal(id2, restored.Mappings["18"]);
        }
        finally
        {
            if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
        }
    }

    [Fact]
    public async Task UnitCrosswalk_TryReadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = await UnitCrosswalk.TryReadAsync("/nonexistent/path/crosswalk.json");
        Assert.Null(result);
    }

    // ──────────── Helper ──────────────────────────────────────────────────

    private static void AssertRow(
        IReadOnlyList<UnitStagingRow> rows,
        int grocyId,
        string code,
        string dim,
        decimal factor,
        UnitMappingAction action,
        bool hasAnomaly)
    {
        var row = rows.First(r => r.GrocyId == grocyId);
        Assert.Equal(code, row.PlantryCode);
        Assert.Equal(dim, row.Dimension);
        Assert.Equal(factor, row.FactorToBase);
        Assert.Equal(action, row.Action);
        if (hasAnomaly)
            Assert.NotNull(row.AnomalyNote);
        else
            Assert.Null(row.AnomalyNote);
    }
}
