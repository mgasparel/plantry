using Plantry.Migration.Grocy;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="ProductStager.ApplyDrops"/> — the cross-page drop-accumulation
/// helper introduced in plantry-c04 to fix drop selections being lost across pagination pages.
///
/// Tests cover:
/// - Rows in droppedIds are stamped IsDropped = true; others are left false.
/// - Merging: IDs from two disjoint sources both get stamped (simulates the page-model
///   union of DroppedProductIds [current page] and DroppedIds [cross-page carry]).
/// - Idempotence: calling ApplyDrops twice with the same set is safe.
/// - Deduplication: an ID appearing in both sources is handled without error.
/// - Empty droppedIds: no row is stamped (fast-path guard).
/// - Non-matching IDs: rows whose GrocyId is not in droppedIds remain un-dropped.
/// </summary>
public sealed class ProductStagerApplyDropsTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static ProductStagingRow Row(int grocyId) =>
        new() { GrocyId = grocyId, GrocyName = $"Product-{grocyId}", PlantryName = $"Product-{grocyId}" };

    private static IReadOnlyList<ProductStagingRow> Rows(params int[] ids) =>
        ids.Select(Row).ToList();

    // ──────────── Tests ───────────────────────────────────────────────────

    [Fact]
    public void Rows_in_dropped_set_are_stamped_IsDropped()
    {
        var rows = Rows(1, 2, 3);

        ProductStager.ApplyDrops(rows, [1, 3]);

        Assert.True(rows[0].IsDropped,  "Row 1 should be dropped");
        Assert.False(rows[1].IsDropped, "Row 2 should not be dropped");
        Assert.True(rows[2].IsDropped,  "Row 3 should be dropped");
    }

    [Fact]
    public void Empty_droppedIds_leaves_all_rows_un_dropped()
    {
        var rows = Rows(10, 20, 30);

        ProductStager.ApplyDrops(rows, []);

        Assert.All(rows, r => Assert.False(r.IsDropped));
    }

    [Fact]
    public void No_rows_match_dropped_ids_leaves_all_un_dropped()
    {
        var rows = Rows(1, 2, 3);

        ProductStager.ApplyDrops(rows, [99, 100]);

        Assert.All(rows, r => Assert.False(r.IsDropped));
    }

    [Fact]
    public void Merge_of_current_page_and_cross_page_ids_stamps_both()
    {
        // Simulates: DroppedProductIds = [1] (current-page Alpine hidden input)
        //            DroppedIds        = [2] (cross-page carry via query string)
        // The page model unions these: MergedDroppedIds() = {1, 2}
        var rows = Rows(1, 2, 3);

        var currentPage  = new[] { 1 };
        var crossPage    = new[] { 2 };
        var merged       = currentPage.Union(crossPage);

        ProductStager.ApplyDrops(rows, merged);

        Assert.True(rows[0].IsDropped,  "Row 1 (current-page drop) should be dropped");
        Assert.True(rows[1].IsDropped,  "Row 2 (cross-page carry) should be dropped");
        Assert.False(rows[2].IsDropped, "Row 3 (not in either set) should not be dropped");
    }

    [Fact]
    public void Duplicate_id_in_merged_set_is_handled_without_error()
    {
        // An ID appearing in both DroppedProductIds and DroppedIds should be deduplicated
        // by the HashSet in MergedDroppedIds; ApplyDrops just stamps once.
        var rows = Rows(5);

        var withDuplicate = new[] { 5, 5, 5 };

        ProductStager.ApplyDrops(rows, withDuplicate);

        Assert.True(rows[0].IsDropped);
    }

    [Fact]
    public void ApplyDrops_is_idempotent()
    {
        var rows = Rows(7, 8);

        ProductStager.ApplyDrops(rows, [7]);
        ProductStager.ApplyDrops(rows, [7]);   // second call — must not throw or change result

        Assert.True(rows[0].IsDropped);
        Assert.False(rows[1].IsDropped);
    }

    [Fact]
    public void Un_checking_a_carried_row_is_not_in_merged_set_leaves_it_un_dropped()
    {
        // Regression guard for the original bug: a row that was carried via droppedIds
        // but then un-checked by the user on the current page must NOT appear in the
        // final merged set (the page model excludes it from DroppedIds for the current page,
        // and it has no Alpine hidden input). ApplyDrops therefore leaves it un-dropped.
        var rows = Rows(10, 20);

        // Row 10 was carried in but user un-checked it → not in either source list.
        // Row 20 was newly checked on the current page.
        ProductStager.ApplyDrops(rows, [20]);

        Assert.False(rows[0].IsDropped, "Row 10 was un-checked; must not be re-dropped");
        Assert.True(rows[1].IsDropped,  "Row 20 was checked; must be dropped");
    }
}
