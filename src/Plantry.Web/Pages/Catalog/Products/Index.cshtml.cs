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

    private DataGridViewModel BuildProductGrid(GridSort? sort)
    {
        IEnumerable<ProductListItem> rows = Products;
        if (sort is { } s)
        {
            rows = s.Key switch
            {
                "name" => s.Descending ? rows.OrderByDescending(p => p.Name) : rows.OrderBy(p => p.Name),
                "category" => s.Descending ? rows.OrderByDescending(p => p.CategoryName) : rows.OrderBy(p => p.CategoryName),
                _ => rows,
            };
        }

        return new DataGridViewModel(
            Id: "products-grid",
            SortUrl: Url.Page("./Index", "SortProducts"),
            CurrentSort: sort,
            Columns:
            [
                new("Name", SortKey: "name"),
                new("Category", SortKey: "category"),
                new("Kind"),
            ],
            Rows: [.. rows.Select(p => new GridRow(
            [
                GridCell.Link(p.Name, $"/Catalog/Products/{p.Id}"),
                GridCell.Text(p.CategoryName ?? ""),
                p switch
                {
                    { IsParent: true } => GridCell.Badge("Parent", BadgeTone.Info),
                    { IsVariant: true } => GridCell.Badge("Variant", BadgeTone.Neutral),
                    _ => GridCell.Muted("—"),
                },
            ]))],
            EmptyMessage: "No products yet — add your first one above.");
    }
}
