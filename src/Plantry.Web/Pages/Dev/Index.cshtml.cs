using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.SharedKernel;
using Plantry.Web.Pages.Shared;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Dev;

/// <summary>
/// Component gallery — a development-only page (gated by DevPagesGateMiddleware) for building
/// and browsing reusable UI primitives in isolation, away from any bounded-context page. Demo
/// data lives entirely on this page; nothing here is wired to Catalog or any other real feature.
/// </summary>
public sealed class IndexModel : PageModel
{
    private static readonly string[] GroceryItems =
    [
        "Apples", "Bananas", "Bread — wholemeal", "Bread — white", "Butter", "Carrots",
        "Cheddar cheese", "Chicken breast", "Eggs", "Flour", "Greek yogurt", "Honey",
        "Milk — whole", "Milk — oat", "Olive oil", "Onions", "Pasta — penne", "Pasta — spaghetti",
        "Potatoes", "Rice — basmati", "Spinach", "Tomatoes", "Tuna — tinned", "Yeast",
    ];

    private static readonly RowListItem[] SortableListDemoItems =
    [
        new("Whole milk", Secondary: "Dairy", Id: "c95d882a-a2c0-4622-8134-462abc6e9044", SortOrder: 0),
        new("Wholemeal bread", Secondary: "Bakery", Id: "7456222a-1d39-4942-9499-d6e5e7ccd58a", SortOrder: 10),
        new("Free-range eggs", Secondary: "Dairy", Id: "3df8d7a5-ab95-414e-9672-66f63bb93804", SortOrder: 20),
        new("Bananas", Secondary: "Produce", Id: "7d38f169-a6fb-46b7-9b07-3f33d318224e", SortOrder: 30),
    ];

    private enum DemoKind { Standalone, Parent, Variant }

    private sealed record DemoProduct(Guid Id, string Name, string Category, DemoKind Kind);

    private static readonly DemoProduct[] DemoProducts =
    [
        new(Guid.Parse("a1d6c8e2-0001-4a10-9c11-000000000001"), "Whole milk", "Dairy", DemoKind.Standalone),
        new(Guid.Parse("a1d6c8e2-0002-4a10-9c11-000000000002"), "Wholemeal bread", "Bakery", DemoKind.Parent),
        new(Guid.Parse("a1d6c8e2-0003-4a10-9c11-000000000003"), "Cheddar cheese", "Dairy", DemoKind.Variant),
        new(Guid.Parse("a1d6c8e2-0004-4a10-9c11-000000000004"), "Bananas", "Produce", DemoKind.Standalone),
        new(Guid.Parse("a1d6c8e2-0005-4a10-9c11-000000000005"), "Penne pasta", "Pantry", DemoKind.Variant),
    ];

    [BindProperty]
    public FieldRowDemoInput FieldRowDemo { get; set; } = new();

    [BindProperty]
    public FormGridDemoInput FormGridDemo { get; set; } = new();

    [BindProperty]
    public SearchableSelectDemoInput SearchableSelectDemo { get; set; } = new();

    public IReadOnlyList<SelectListItem> UnitOptions { get; } =
        new[] { "g", "kg", "ml", "L", "each" }
            .Select(u => new SelectListItem(u, u))
            .ToList();

    public IReadOnlyList<SelectListItem> GroceryOptions { get; } =
        GroceryItems.Select(g => new SelectListItem(g, g)).ToList();

    public RowListViewModel PopulatedList { get; } = new(
        [
            new RowListItem("Whole milk", Href: "#", Secondary: "Dairy · default unit L"),
            new RowListItem("Wholemeal bread", Href: "#", Secondary: "Bakery · default unit each", Meta: "parent (groups variants)"),
            new RowListItem("Cheddar cheese", Href: "#", Secondary: "Dairy · default unit g", Meta: "variant"),
        ],
        EmptyMessage: "No products yet — add your first one above.");

    public RowListViewModel EmptyList { get; } = new([], EmptyMessage: "Nothing here yet — this is the empty state.");

    /// <summary>Built in OnGet rather than as a property initializer — Url.Page needs the page context, which isn't available yet during construction.</summary>
    public RowListViewModel SortableListDemo { get; private set; } = null!;

    /// <summary>Built in OnGet (Url.Page needs page context). Re-built by OnGetSortProducts for the htmx sort swap.</summary>
    public DataGridViewModel ProductGrid { get; private set; } = null!;

    public sealed class FieldRowDemoInput
    {
        [Required, MaxLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Range(0, 9999)]
        [Display(Name = "Quantity on hand")]
        public decimal? Quantity { get; set; }

        [Display(Name = "Default unit")]
        public string? Unit { get; set; }
    }

    public sealed class FormGridDemoInput
    {
        [Required, MaxLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Category")]
        public string? Category { get; set; }

        [Display(Name = "Default unit")]
        public string? Unit { get; set; }

        [Range(0, 9999)]
        [Display(Name = "Restock level")]
        public decimal? Par { get; set; }
    }

    public sealed class SearchableSelectDemoInput
    {
        [Display(Name = "Grocery item")]
        public string? Item { get; set; }

        /// <summary>Backs the fuzzy-ranked + create demo (plantry-gzro.1) — a separate field so the
        /// two <searchable-select> demos on this page don't collide over the same posted value.</summary>
        [Display(Name = "Grocery item (fuzzy + create)")]
        public string? FuzzyItem { get; set; }
    }

    public void OnGet()
    {
        SortableListDemo = new RowListViewModel(
            SortableListDemoItems,
            EmptyMessage: "Nothing to reorder yet — drag items here.",
            Reorderable: true,
            ReorderUrl: Url.Page("./Index", "ReorderDemo")!);

        ProductGrid = BuildProductGrid(sort: null);
    }

    /// <summary>Backs the data-grid demo's sortable headers — re-orders the demo rows server-side and returns the whole grid re-rendered (Option B swaps the entire table on sort).</summary>
    public IActionResult OnGetSortProducts(string sort, bool desc) =>
        Partial("Shared/_DataGrid", BuildProductGrid(new GridSort(sort, desc)));

    /// <summary>Backs the data-grid demo's delete action — accepts the POST but stores nothing (this page has no backing storage); the row vanishes via the action's hx-swap, and a reload restores the seeded set.</summary>
    public IActionResult OnPostDeleteProductDemo(Guid id) => new OkResult();

    private DataGridViewModel BuildProductGrid(GridSort? sort)
    {
        IEnumerable<DemoProduct> rows = DemoProducts;
        if (sort is { } s)
        {
            rows = s.Key switch
            {
                "name" => s.Descending ? rows.OrderByDescending(p => p.Name) : rows.OrderBy(p => p.Name),
                "category" => s.Descending ? rows.OrderByDescending(p => p.Category) : rows.OrderBy(p => p.Category),
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
                new("", GridAlign.End),
            ],
            Rows: rows.Select(p => new GridRow(
            [
                GridCell.Link(p.Name, $"/Catalog/Products/{p.Id}"),
                GridCell.Text(p.Category),
                p.Kind switch
                {
                    DemoKind.Parent => GridCell.Badge("Parent", BadgeTone.Info),
                    DemoKind.Variant => GridCell.Badge("Variant", BadgeTone.Neutral),
                    _ => GridCell.Muted("—"),
                },
                GridCell.Actions(
                    GridAction.Icon("Edit product details", $"/Catalog/Products/{p.Id}", "i-edit"),
                    GridAction.Post("Delete", Url.Page("./Index", "DeleteProductDemo", new { id = p.Id })!,
                        confirm: $"Delete {p.Name}?", removesRow: true)),
            ])).ToList(),
            EmptyMessage: "No products yet — add your first one above.");
    }

    /// <summary>Backs the sortable-list demo's drag-and-drop persist call — accepts the dragged order but doesn't store it (this page has no backing storage), so a reload always returns to the seeded order.</summary>
    public IActionResult OnPostReorderDemo(List<Guid> ids) => new OkResult();

    /// <summary>Backs the searchable-select demo's hx-get — filters the demo grocery list server-side and returns replacement &lt;li&gt; option markup.</summary>
    public ContentResult OnGetFilterGroceries(string? q)
    {
        var matches = GroceryOptions
            .Where(item => string.IsNullOrWhiteSpace(q) || item.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(10);

        var html = new StringBuilder();
        SearchableSelectTagHelper.AppendOptions(html, matches, HtmlEncoder.Default);
        return Content(html.ToString(), "text/html");
    }

    /// <summary>
    /// Backs the fuzzy-search + create demo's hx-get (plantry-gzro.1) — ranks the same demo grocery
    /// list with <see cref="ProductNameMatcher"/> (the deterministic ranker Recipes/TakeStock's real
    /// search handlers use) and returns replacement &lt;li&gt; option markup carrying a <c>.rk</c>
    /// best/N% label, mirroring Recipes' <c>OnGetSearchProductsAsync</c> rendering convention.
    /// </summary>
    public ContentResult OnGetFuzzySearchGroceries(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var ranked = ProductNameMatcher.Rank(GroceryItems, name => name, q.Trim());

        var html = new StringBuilder();
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            var label = ProductNameMatcher.RankLabel(r.Score, isTopHit: i == 0);
            html.Append(ProductSearchOptionRenderer.RenderSelectOption(r.Name, r.Name, label));
        }
        return Content(html.ToString(), "text/html");
    }
}
