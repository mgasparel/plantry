using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Application;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class IndexModel(ProductQueryService products) : PageModel
{
    public IReadOnlyList<ProductListItem> Products { get; private set; } = [];
    public DataGridViewModel ProductGrid { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Products = await products.ListActiveAsync();
        ProductGrid = BuildProductGrid(sort: null);
    }

    public async Task<IActionResult> OnGetSortProductsAsync(string sort, bool desc)
    {
        Products = await products.ListActiveAsync();
        ProductGrid = BuildProductGrid(new GridSort(sort, desc));
        return Partial("Shared/_DataGrid", ProductGrid);
    }

    /// <summary>Sort-key selectors for the products grid, keyed by the column's <c>SortKey</c>. Mirrors the
    /// pantry grid's shape for consistency; boxing to <see cref="IComparable"/> sorts identically to a
    /// typed <c>OrderBy</c>.</summary>
    private static readonly Dictionary<string, Func<ProductListItem, IComparable?>> ProductSortKeys = new()
    {
        ["name"]     = p => p.Name,
        ["category"] = p => p.CategoryName,
    };

    /// <summary>Applies the requested column sort; unknown or absent keys leave order untouched.
    /// <c>internal</c> so it can be unit-tested directly (no page-handler test harness exists).</summary>
    internal static IEnumerable<ProductListItem> ApplyProductSort(IEnumerable<ProductListItem> rows, GridSort? sort) =>
        sort is { } s && ProductSortKeys.TryGetValue(s.Key, out var key)
            ? (s.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            : rows;

    private static GridRow BuildProductRow(ProductListItem p) => new(
    [
        GridCell.Link(p.Name, $"/Catalog/Products/{p.Id}"),
        GridCell.Text(p.CategoryName ?? ""),
        p switch
        {
            { IsParent: true } => GridCell.Badge("Parent", BadgeTone.Info),
            { IsVariant: true } => GridCell.Badge("Variant", BadgeTone.Neutral),
            _ => GridCell.Muted("—"),
        },
    ]);

    private DataGridViewModel BuildProductGrid(GridSort? sort) =>
        new DataGridViewModel(
            Id: "products-grid",
            SortUrl: Url.Page("./Index", "SortProducts"),
            CurrentSort: sort,
            Columns:
            [
                new("Name", SortKey: "name"),
                new("Category", SortKey: "category"),
                new("Kind"),
            ],
            Rows: [.. ApplyProductSort(Products, sort).Select(BuildProductRow)],
            EmptyMessage: "No products yet — add your first one above.");
}
