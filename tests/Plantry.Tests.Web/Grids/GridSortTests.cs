using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Web.Pages.Shared;
using PantryPage = Plantry.Web.Pages.Pantry.IndexModel;
using ProductsPage = Plantry.Web.Pages.Catalog.Products.IndexModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Safety-net unit tests for the grid sort helpers extracted from <c>BuildPantryGrid</c> /
/// <c>BuildProductGrid</c> during the complexity refactor (plantry-6rl). No page-handler test
/// harness existed for these pages, so the sort logic — the largest slice of the original CCN —
/// is now pinned directly. Each test asserts key selection and both directions.
/// </summary>
public sealed class GridSortTests
{
    private static PantryListItem Pantry(
        string name, string? category = null, string? location = null,
        bool isVariant = false, decimal qty = 0m, DateOnly? expiry = null) =>
        new(
            ProductId: Guid.NewGuid(),
            Name: name,
            CategoryName: category,
            LocationDisplay: location,
            IsVariant: isVariant,
            TotalQuantity: qty,
            DisplayUnitCode: "ea",
            LotCount: 0,
            SoonestExpiry: expiry,
            ExpiryTone: ExpiryTone.None);

    private static ProductListItem Product(string name, string? category = null) =>
        new(
            Id: ProductId.New(),
            Name: name,
            CategoryName: category,
            DefaultUnitCode: "ea",
            IsArchived: false,
            IsVariant: false,
            IsParent: false);

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

    // ---- Products ----------------------------------------------------------

    [Fact]
    public void Products_null_sort_preserves_input_order()
    {
        var rows = new[] { Product("banana"), Product("apple") };

        var sorted = ProductsPage.ApplyProductSort(rows, null).ToList();

        Assert.Equal(new[] { "banana", "apple" }, sorted.Select(r => r.Name));
    }

    [Fact]
    public void Products_unknown_key_preserves_input_order()
    {
        var rows = new[] { Product("banana"), Product("apple") };

        var sorted = ProductsPage.ApplyProductSort(rows, new GridSort("kind", Descending: false)).ToList();

        Assert.Equal(new[] { "banana", "apple" }, sorted.Select(r => r.Name));
    }

    [Theory]
    [InlineData(false, new[] { "apple", "banana", "cherry" })]
    [InlineData(true, new[] { "cherry", "banana", "apple" })]
    public void Products_sorts_by_name(bool descending, string[] expected)
    {
        var rows = new[] { Product("banana"), Product("apple"), Product("cherry") };

        var sorted = ProductsPage.ApplyProductSort(rows, new GridSort("name", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.Name));
    }

    [Theory]
    [InlineData(false, new[] { "a", "b", "c" })]
    [InlineData(true, new[] { "c", "b", "a" })]
    public void Products_sorts_by_category(bool descending, string[] expected)
    {
        var rows = new[]
        {
            Product("x", category: "b"), Product("y", category: "a"), Product("z", category: "c"),
        };

        var sorted = ProductsPage.ApplyProductSort(rows, new GridSort("category", descending)).ToList();

        Assert.Equal(expected, sorted.Select(r => r.CategoryName));
    }
}
