using Plantry.Migration.Grocy;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="RecipeStager.ApplyDrops"/> — the cross-page drop-accumulation
/// helper introduced in plantry-c04 to fix drop selections being lost across pagination pages.
///
/// Mirrors <see cref="ProductStagerApplyDropsTests"/> for the recipe-side behaviour.
/// </summary>
public sealed class RecipeStagerApplyDropsTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static RecipeStagingRow Row(int grocyId) =>
        new() { GrocyId = grocyId, GrocyName = $"Recipe-{grocyId}", PlantryName = $"Recipe-{grocyId}" };

    private static IReadOnlyList<RecipeStagingRow> Rows(params int[] ids) =>
        ids.Select(Row).ToList();

    // ──────────── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Rows_in_dropped_set_are_stamped_IsDropped()
    {
        var rows = Rows(1, 2, 3);

        RecipeStager.ApplyDrops(rows, [1, 3]);

        Assert.True(rows[0].IsDropped,  "Row 1 should be dropped");
        Assert.False(rows[1].IsDropped, "Row 2 should not be dropped");
        Assert.True(rows[2].IsDropped,  "Row 3 should be dropped");
    }

    [Fact]
    public void Empty_droppedIds_leaves_all_rows_un_dropped()
    {
        var rows = Rows(10, 20, 30);

        RecipeStager.ApplyDrops(rows, []);

        Assert.All(rows, r => Assert.False(r.IsDropped));
    }

    [Fact]
    public void No_rows_match_dropped_ids_leaves_all_un_dropped()
    {
        var rows = Rows(1, 2, 3);

        RecipeStager.ApplyDrops(rows, [99, 100]);

        Assert.All(rows, r => Assert.False(r.IsDropped));
    }

    [Fact]
    public void Merge_of_current_page_and_cross_page_ids_stamps_both()
    {
        var rows = Rows(1, 2, 3);

        var currentPage = new[] { 1 };
        var crossPage   = new[] { 2 };
        var merged      = currentPage.Union(crossPage);

        RecipeStager.ApplyDrops(rows, merged);

        Assert.True(rows[0].IsDropped,  "Row 1 (current-page drop) should be dropped");
        Assert.True(rows[1].IsDropped,  "Row 2 (cross-page carry) should be dropped");
        Assert.False(rows[2].IsDropped, "Row 3 (not in either set) should not be dropped");
    }

    [Fact]
    public void Duplicate_id_in_merged_set_is_handled_without_error()
    {
        var rows = Rows(5);

        RecipeStager.ApplyDrops(rows, [5, 5, 5]);

        Assert.True(rows[0].IsDropped);
    }

    [Fact]
    public void ApplyDrops_is_idempotent()
    {
        var rows = Rows(7, 8);

        RecipeStager.ApplyDrops(rows, [7]);
        RecipeStager.ApplyDrops(rows, [7]);

        Assert.True(rows[0].IsDropped);
        Assert.False(rows[1].IsDropped);
    }

    [Fact]
    public void Un_checking_a_carried_row_is_not_in_merged_set_leaves_it_un_dropped()
    {
        var rows = Rows(10, 20);

        // Row 10 was carried but un-checked; row 20 newly checked.
        RecipeStager.ApplyDrops(rows, [20]);

        Assert.False(rows[0].IsDropped, "Row 10 was un-checked; must not be re-dropped");
        Assert.True(rows[1].IsDropped,  "Row 20 was checked; must be dropped");
    }
}
