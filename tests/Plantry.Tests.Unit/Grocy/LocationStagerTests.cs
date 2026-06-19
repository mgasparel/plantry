using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="LocationStager"/> — the Grocy location staging algorithm
/// (plantry-zcw.9).
///
/// Tests cover:
/// - is_freezer == 1 → LocationType = "frozen"
/// - is_freezer == 0 → LocationType = "ambient"
/// - Exact name match → MatchExisting, Auto
/// - Fuzzy contains match → MatchExisting, Auto
/// - Ambiguous match → MatchExisting, NeedsReview
/// - No match → CreateNew, NeedsReview
/// - Rows are returned in Grocy-id order
/// </summary>
public sealed class LocationStagerTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static GrocyManifest ManifestWith(IEnumerable<GrocyLocation> locations) =>
        new() { ExtractedAt = DateTimeOffset.UtcNow, Locations = locations.ToList() };

    private static GrocyLocation Loc(int id, string name, int isFreezer = 0) =>
        new(id, name, null, isFreezer, null);

    // ──────────── IsFreezer flag ─────────────────────────────────────────

    [Fact]
    public void IsFreezer_True_AssignsFrozenLocationType()
    {
        var manifest = ManifestWith([Loc(1, "Freezer", isFreezer: 1)]);

        var rows = LocationStager.Stage(manifest, []);

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.IsFreezer);
        Assert.Equal("frozen", row.LocationType);
    }

    [Fact]
    public void IsFreezer_False_AssignsAmbientLocationType()
    {
        var manifest = ManifestWith([Loc(2, "Pantry", isFreezer: 0)]);

        var rows = LocationStager.Stage(manifest, []);

        Assert.Single(rows);
        var row = rows[0];
        Assert.False(row.IsFreezer);
        Assert.Equal("ambient", row.LocationType);
    }

    // ──────────── Name matching ───────────────────────────────────────────

    [Fact]
    public void ExactNameMatch_CaseInsensitive_ReturnsMatchExistingAuto()
    {
        var manifest = ManifestWith([Loc(3, "Fridge")]);
        var existing = new[] { "fridge" };

        var rows = LocationStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("fridge", row.PlantryName);
        Assert.Equal(LocationMappingAction.MatchExisting, row.Action);
        Assert.Equal(LocationStagingStatus.Auto, row.Status);
        Assert.Null(row.AnomalyNote);
    }

    [Fact]
    public void FuzzyContainsMatch_ReturnsMatchExistingAuto()
    {
        var manifest = ManifestWith([Loc(4, "Fridge")]);
        var existing = new[] { "Main Fridge" };

        var rows = LocationStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("Main Fridge", row.PlantryName);
        Assert.Equal(LocationMappingAction.MatchExisting, row.Action);
        Assert.Equal(LocationStagingStatus.Auto, row.Status);
        Assert.Null(row.AnomalyNote);
    }

    [Fact]
    public void AmbiguousMatch_FlagsNeedsReview()
    {
        var manifest = ManifestWith([Loc(5, "Fridge")]);
        var existing = new[] { "Main Fridge", "Second Fridge" };

        var rows = LocationStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(LocationMappingAction.MatchExisting, row.Action);
        Assert.Equal(LocationStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
        Assert.Contains("Main Fridge", row.AnomalyNote);
        Assert.Contains("Second Fridge", row.AnomalyNote);
    }

    [Fact]
    public void NoMatch_ReturnsCreateNewNeedsReview()
    {
        var manifest = ManifestWith([Loc(6, "Cellar")]);
        var existing = new[] { "Fridge", "Pantry" };

        var rows = LocationStager.Stage(manifest, existing);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("Cellar", row.PlantryName);
        Assert.Equal(LocationMappingAction.CreateNew, row.Action);
        Assert.Equal(LocationStagingStatus.NeedsReview, row.Status);
        Assert.NotNull(row.AnomalyNote);
    }

    [Fact]
    public void NoMatch_PreservesIsFreezerLocationType()
    {
        // Even unmatched freezer locations should get LocationType = "frozen"
        var manifest = ManifestWith([Loc(7, "Deep Freeze", isFreezer: 1)]);

        var rows = LocationStager.Stage(manifest, []);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("frozen", row.LocationType);
        Assert.True(row.IsFreezer);
        Assert.Equal(LocationMappingAction.CreateNew, row.Action);
    }

    // ──────────── Ordering ───────────────────────────────────────────────

    [Fact]
    public void Stage_ReturnsRowsInGrocyIdOrder()
    {
        var manifest = ManifestWith(
        [
            Loc(9, "Freezer", isFreezer: 1),
            Loc(2, "Pantry"),
            Loc(5, "Fridge"),
        ]);

        var rows = LocationStager.Stage(manifest, []);

        Assert.Equal([2, 5, 9], rows.Select(r => r.GrocyId).ToList());
    }

    // ──────────── Crosswalk helper ────────────────────────────────────────

    [Fact]
    public void LocationCrosswalk_ResolvePath_UsesSameDirectoryAsManifest()
    {
        var manifestPath  = Path.Combine("C:", "Plantry", "grocy-manifest.json");
        var crosswalkPath = LocationCrosswalk.ResolvePath(manifestPath);

        Assert.Equal(Path.Combine("C:", "Plantry", "location-crosswalk.json"), crosswalkPath);
    }

    [Fact]
    public async Task LocationCrosswalk_WriteAndRead_RoundTrips()
    {
        var tempDir       = Path.GetTempPath();
        var crosswalkPath = Path.Combine(tempDir, $"location-crosswalk-test-{Guid.NewGuid()}.json");

        try
        {
            var id1 = Guid.NewGuid();
            var crosswalk = new LocationCrosswalk
            {
                CommittedAt = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero),
                Mappings    = new Dictionary<string, Guid>
                {
                    ["3"] = id1,
                },
            };

            await crosswalk.WriteAsync(crosswalkPath);
            var restored = await LocationCrosswalk.TryReadAsync(crosswalkPath);

            Assert.NotNull(restored);
            Assert.Equal("1.0", restored.Version);
            Assert.Single(restored.Mappings);
            Assert.Equal(id1, restored.Mappings["3"]);
        }
        finally
        {
            if (File.Exists(crosswalkPath)) File.Delete(crosswalkPath);
        }
    }
}
