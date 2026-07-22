using Plantry.Inventory.Application;
using Plantry.Web.Pages.Shared;
using PantryPage = Plantry.Web.Pages.Pantry.IndexModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Safety-net unit tests for the grid sort helpers extracted from <c>BuildPantryGrid</c> during the
/// complexity refactor (plantry-6rl). No page-handler test harness existed for this page, so the sort
/// logic — the largest slice of the original CCN — is pinned directly. Each test asserts key
/// selection and both directions.
///
/// The Catalog Products grid's own sort tests (<c>ApplyProductSort</c>/<c>BuildProductGrid</c>) were
/// retired alongside that page (plantry-sjfn) — Catalog Products was absorbed into the unified Pantry
/// list, and <c>/Catalog/Products</c> is now just a redirect stub with no grid of its own.
/// </summary>
public sealed class GridSortTests
{
    private static PantryListItem Pantry(
        string name, string? category = null, string? location = null,
        bool isVariant = false, decimal qty = 0m, DateOnly? expiry = null,
        bool isStocked = true, bool isParent = false, int? lotCount = null) =>
        new(
            ProductId: Guid.NewGuid(),
            Name: name,
            CategoryName: category,
            LocationDisplay: location,
            IsVariant: isVariant,
            TotalQuantity: qty,
            DisplayUnitCode: "ea",
            LotCount: lotCount ?? (isStocked ? 1 : 0),
            SoonestExpiry: expiry,
            ExpiryTone: ExpiryTone.None,
            IsStocked: isStocked,
            IsParent: isParent);

    // ---- Pantry ------------------------------------------------------------

    [Fact]
    public void Pantry_null_sort_preserves_input_order()
    {
        var rows = new[] { Pantry("banana"), Pantry("apple"), Pantry("cherry") };

        var sorted = PantryPage.ApplyPantrySort(rows, null).ToList();

        Assert.Equal(new[] { "banana", "apple", "cherry" }, sorted.Select(r => r.Name));
    }

    [Fact]
    public void Pantry_unknown_key_preserves_input_order()
    {
        var rows = new[] { Pantry("banana"), Pantry("apple") };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("nope", Descending: false)).ToList();

        Assert.Equal(new[] { "banana", "apple" }, sorted.Select(r => r.Name));
    }

    [Theory]
    [InlineData(false, new[] { "apple", "banana", "cherry" })]
    [InlineData(true, new[] { "cherry", "banana", "apple" })]
    public void Pantry_sorts_by_name(bool descending, string[] expected)
    {
        var rows = new[] { Pantry("banana"), Pantry("apple"), Pantry("cherry") };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("name", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.Name));
    }

    [Theory]
    [InlineData(false, new[] { "a", "b", "c" })]
    [InlineData(true, new[] { "c", "b", "a" })]
    public void Pantry_sorts_by_category(bool descending, string[] expected)
    {
        var rows = new[]
        {
            Pantry("x", category: "b"), Pantry("y", category: "a"), Pantry("z", category: "c"),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("category", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.CategoryName));
    }

    [Theory]
    [InlineData(false, new[] { null, "Fridge", "Pantry" })]
    [InlineData(true, new[] { "Pantry", "Fridge", null })]
    public void Pantry_sorts_by_location_nulls_first_ascending(bool descending, string?[] expected)
    {
        var rows = new[]
        {
            Pantry("x", location: "Pantry"), Pantry("y", location: null), Pantry("z", location: "Fridge"),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("location", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.LocationDisplay));
    }

    [Theory]
    [InlineData(false, new[] { false, false, true })]
    [InlineData(true, new[] { true, false, false })]
    public void Pantry_sorts_by_kind(bool descending, bool[] expected)
    {
        var rows = new[]
        {
            Pantry("x", isVariant: false), Pantry("y", isVariant: true), Pantry("z", isVariant: false),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("kind", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.IsVariant));
    }

    [Theory]
    [InlineData(false, new[] { 1.0, 2.0, 3.0 })]
    [InlineData(true, new[] { 3.0, 2.0, 1.0 })]
    public void Pantry_sorts_by_quantity(bool descending, double[] expected)
    {
        var rows = new[]
        {
            Pantry("x", qty: 2m), Pantry("y", qty: 3m), Pantry("z", qty: 1m),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("qty", descending)).ToList();

        Assert.Equal(expected.Select(d => (decimal)d), sorted.Select(r => r.TotalQuantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pantry_sorts_by_expiry_nulls_first_ascending(bool descending)
    {
        var early = new DateOnly(2026, 1, 1);
        var late = new DateOnly(2026, 12, 31);
        var rows = new[]
        {
            Pantry("x", expiry: late), Pantry("y", expiry: null), Pantry("z", expiry: early),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("expiry", descending)).ToList();

        DateOnly?[] expected = descending
            ? new DateOnly?[] { late, early, null }
            : new DateOnly?[] { null, early, late };
        Assert.Equal(expected, sorted.Select(r => r.SoonestExpiry));
    }

    // ---- Everything scope (plantry-sjfn): unstocked rows trail the qty sort ------------------

    [Theory]
    [InlineData(false, new[] { "low", "high", "unstocked" })]
    [InlineData(true, new[] { "high", "low", "unstocked" })]
    public void Pantry_qty_sort_puts_unstocked_rows_last_regardless_of_direction(bool descending, string[] expected)
    {
        var rows = new[]
        {
            Pantry("unstocked", qty: 0m, isStocked: false),
            Pantry("high", qty: 3m),
            Pantry("low", qty: 1m),
        };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("qty", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.Name));
    }

    [Fact]
    public void Pantry_qty_sort_with_no_unstocked_rows_behaves_like_before()
    {
        var rows = new[] { Pantry("x", qty: 2m), Pantry("y", qty: 3m), Pantry("z", qty: 1m) };

        var sorted = PantryPage.ApplyPantrySort(rows, new GridSort("qty", Descending: false)).ToList();

        Assert.Equal(new[] { "z", "x", "y" }, sorted.Select(r => r.Name));
    }

    [Fact]
    public void Pantry_kind_cell_renders_parent_badge_for_synthesized_parent_rows()
    {
        var parent = Pantry("Maple syrup", isStocked: false, isParent: true);

        var cell = PantryPage.KindCell(parent);

        Assert.Equal(GridCellKind.Badge, cell.Kind);
        Assert.Equal("Parent", cell.Value);
        Assert.Equal(BadgeTone.Info, cell.Tone);
    }

    [Fact]
    public void Pantry_kind_cell_still_renders_variant_badge()
    {
        var variant = Pantry("Frozen peas", isVariant: true);

        var cell = PantryPage.KindCell(variant);

        Assert.Equal(GridCellKind.Badge, cell.Kind);
        Assert.Equal("Variant", cell.Value);
        Assert.Equal(BadgeTone.Neutral, cell.Tone);
    }

    [Fact]
    public void Pantry_kind_cell_muted_when_neither_parent_nor_variant()
    {
        var plain = Pantry("Olive oil");

        var cell = PantryPage.KindCell(plain);

        Assert.Equal(GridCellKind.Muted, cell.Kind);
    }
}
