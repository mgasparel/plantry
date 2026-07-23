using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Web.Pages.Recipes;

namespace Plantry.Tests.Web;

/// <summary>
/// Pins <see cref="DetailsModel.ComputeInclusionRollup"/> (plantry-j4cx, plantry-4037) — the pure,
/// synchronous worst-of-children roll-up math extracted from <c>BuildInclusionRow</c>: worst-of status
/// ladder, distinct (ProductId, UnitId) dedup, chip selection, and the soonest expiry across ALL children.
/// A pure static method — L1, no host needed. Rendered behavior stays pinned by the existing L4
/// <see cref="RecipeInclusionRollupRowTests"/>.
/// </summary>
public sealed class ComputeInclusionRollupTests
{
    private static readonly Guid ProductA = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid ProductB = Guid.Parse("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid UnitGram = Guid.Parse("b2000000-0000-0000-0000-000000000001");
    private static readonly Guid UnitEach = Guid.Parse("b2000000-0000-0000-0000-000000000002");

    /// <summary>
    /// Builds one index-aligned (item, line) pair. <paramref name="unitId"/> defaults to <see
    /// cref="UnitGram"/> — irrelevant to most cases except the dedup-by-unit test.
    /// </summary>
    private static (IngredientItemView Item, ExpandedLine Line) Child(
        Guid productId, IngredientStatus status, bool isUntracked = false, int? expiresWithinDays = null,
        Guid? unitId = null)
    {
        var uid = unitId ?? UnitGram;
        var item = new IngredientItemView(
            ProductName: "Product",
            ProductId: productId,
            Quantity: 1m,
            UnitCode: "g",
            IsUntracked: isUntracked,
            Status: status,
            ExpiresWithinDays: expiresWithinDays);
        var line = new ExpandedLine(
            Path: [],
            IngredientId: IngredientId.New(),
            SourceRecipeId: RecipeId.New(),
            ProductId: productId,
            Quantity: 1m,
            UnitId: isUntracked ? null : uid,
            GroupPath: []);
        return (item, line);
    }

    private static (IReadOnlyList<IngredientItemView> Items, IReadOnlyList<ExpandedLine> Lines) Split(
        params (IngredientItemView Item, ExpandedLine Line)[] children) =>
        (children.Select(c => c.Item).ToList(), children.Select(c => c.Line).ToList());

    // ── Worst-of ordering ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Any_missing_child_makes_the_whole_rollup_missing()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.Missing));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(IngredientStatus.Missing, result.WorstStatus);
    }

    [Fact]
    public void No_missing_but_any_low_child_makes_the_rollup_low()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.Low));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(IngredientStatus.Low, result.WorstStatus);
    }

    [Fact]
    public void All_in_stock_children_make_the_rollup_in_stock()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.InStock));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(IngredientStatus.InStock, result.WorstStatus);
    }

    // ── All-untracked ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_untracked_children_yield_untracked_status_zero_tracked_total_and_no_chip()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.Untracked, isUntracked: true),
            Child(ProductB, IngredientStatus.Untracked, isUntracked: true));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(IngredientStatus.Untracked, result.WorstStatus);
        Assert.Equal(0, result.TrackedTotal);
        Assert.Equal(0, result.InStockCount);
        Assert.Null(result.Chip);
    }

    // ── Distinct-key dedup ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Same_product_and_unit_repeated_counts_once_in_total_and_in_status_tally()
    {
        // Two lines, same (ProductId, UnitId), both Missing — should count as ONE tracked, ONE missing,
        // not two, so the "N of M" denominator isn't inflated by a sub listing the same product twice.
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.Missing),
            Child(ProductA, IngredientStatus.Missing));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(1, result.TrackedTotal);
        Assert.Equal(IngredientStatus.Missing, result.WorstStatus);
        Assert.Equal("1 to buy", result.Chip?.Label);
    }

    [Fact]
    public void Same_product_with_different_units_counts_twice()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock, unitId: UnitGram),
            Child(ProductA, IngredientStatus.InStock, unitId: UnitEach));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(2, result.TrackedTotal);
        Assert.Equal(2, result.InStockCount);
    }

    // ── Untracked exclusion ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Untracked_children_are_excluded_from_dedup_and_counts_entirely()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.Untracked, isUntracked: true));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(1, result.TrackedTotal);
        Assert.Equal(1, result.InStockCount);
        Assert.Equal(IngredientStatus.InStock, result.WorstStatus);
    }

    // ── Chip selection ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Chip_prefers_missing_count_over_low_count_when_both_present()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.Missing),
            Child(ProductB, IngredientStatus.Low));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal("1 to buy", result.Chip?.Label);
        Assert.Equal(IngredientStatus.Missing, result.Chip?.Tone);
    }

    [Fact]
    public void Chip_is_the_low_count_when_no_child_is_missing()
    {
        var productC = Guid.Parse("b1000000-0000-0000-0000-000000000003");
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.Low),
            Child(productC, IngredientStatus.Low));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal("2 low", result.Chip?.Label);
        Assert.Equal(IngredientStatus.Low, result.Chip?.Tone);
    }

    [Fact]
    public void Chip_is_null_when_every_tracked_child_is_fully_in_stock()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.InStock));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Null(result.Chip);
    }

    // ── Expiry: Min over ALL children, not the deduped set ────────────────────────────────────

    [Fact]
    public void Worst_expiry_is_the_minimum_across_every_child_including_duplicate_keys()
    {
        // Same (ProductId, UnitId) twice — deduped down to ONE tracked count — but the expiry Min must
        // still consider BOTH child lines' ExpiresWithinDays, not just the surviving deduped entry.
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock, expiresWithinDays: 5),
            Child(ProductA, IngredientStatus.InStock, expiresWithinDays: 2));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(2, result.WorstExpiresWithinDays);
    }

    [Fact]
    public void Worst_expiry_is_null_when_no_child_has_an_expiry_value()
    {
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock),
            Child(ProductB, IngredientStatus.Low));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Null(result.WorstExpiresWithinDays);
    }

    [Fact]
    public void Worst_expiry_considers_an_untracked_childs_expiry_too()
    {
        // ExpiresWithinDays is read from ALL children in the source (untracked staples excluded only from
        // the dedup/status tally, not from the expiry Min) — pin that an untracked child with an expiry
        // still contributes to the soonest-expiry surfaced while collapsed.
        var (items, lines) = Split(
            Child(ProductA, IngredientStatus.InStock, expiresWithinDays: 10),
            Child(ProductB, IngredientStatus.Untracked, isUntracked: true, expiresWithinDays: 3));

        var result = DetailsModel.ComputeInclusionRollup(items, lines);

        Assert.Equal(3, result.WorstExpiresWithinDays);
    }
}
