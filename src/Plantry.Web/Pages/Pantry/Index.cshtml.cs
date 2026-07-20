using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Pantry;

[Authorize]
public sealed class IndexModel(
    InventoryQueryService queries,
    ICatalogReadFacade catalog,
    Plantry.Inventory.Domain.IProductStockRepository stocks,
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddStockCommand> addStockLogger) : PageModel
{
    public DataGridViewModel PantryGrid { get; private set; } = null!;

    public IReadOnlyList<SelectListItem> ProductOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];

    public IReadOnlyList<SelectListItem> FilterCategoryOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> FilterLocationOptions { get; private set; } = [];

    [BindProperty]
    public AddStockInputModel Input { get; set; } = new();

    public sealed class AddStockInputModel
    {
        [Required(ErrorMessage = "Choose a product.")]
        public Guid? ProductId { get; set; }

        [Required(ErrorMessage = "Enter a quantity.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal? Quantity { get; set; }

        [Required(ErrorMessage = "Choose a unit.")]
        public Guid? UnitId { get; set; }

        [Required(ErrorMessage = "Choose a location.")]
        public Guid? LocationId { get; set; }

        [DataType(DataType.Date)]
        public DateOnly? ExpiryDate { get; set; }
    }

    public async Task OnGetAsync()
    {
        var items = await queries.ListPantryAsync();
        PantryGrid = BuildPantryGrid(items);
        FilterCategoryOptions = (await categories.ListActiveAsync())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SelectListItem(c.Name, c.Name))
            .ToList();
        FilterLocationOptions = (await locations.ListActiveAsync())
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new SelectListItem(l.Name, l.Name))
            .ToList();
    }

    public async Task<IActionResult> OnGetFilterAsync(string? q, string? expiry, string? category, string? location, bool hideVariants)
    {
        var all = await queries.ListPantryAsync();
        var items = Filter(all, q, expiry, category, location, hideVariants);
        return Partial("_PantryList", new PantryListPartialModel(BuildPantryGrid(items), Oob: false));
    }

    public async Task<IActionResult> OnGetSortPantryAsync(string sort, bool desc)
    {
        var items = await queries.ListPantryAsync();
        var grid = BuildPantryGrid(items, new GridSort(sort, desc));
        return Partial("Shared/_DataGrid", grid);
    }

    public async Task<IActionResult> OnGetAddSheetAsync()
    {
        await LoadAddOptionsAsync();
        return Partial("_AddStockSheet", this);
    }

    public async Task<ContentResult> OnGetFilterProductsAsync(string? q)
    {
        var matches = (await catalog.ListProductsAsync())
            .Where(p => p.CanHoldStock)
            .Where(p => string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()));

        var html = new StringBuilder();
        SearchableSelectTagHelper.AppendOptions(html, matches, HtmlEncoder.Default);
        return Content(html.ToString(), "text/html");
    }

    public async Task<IActionResult> OnPostAddStockAsync()
    {
        if (!ModelState.IsValid)
            return await ReloadSheetAsync();

        var expiry = await ResolveExpiryAsync(Input.ProductId!.Value, Input.ExpiryDate);

        var cmd = new AddStockCommand(
            Input.ProductId!.Value, Input.Quantity!.Value, Input.UnitId!.Value, Input.LocationId!.Value,
            CurrentUserId, skuId: null, expiryDate: expiry, purchasedAt: Today(),
            stocks, catalog, clock, tenant, logger: addStockLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadSheetAsync();
        }

        var items = await queries.ListPantryAsync();
        return Partial("_PantryList", new PantryListPartialModel(BuildPantryGrid(items), Oob: true));
    }

    /// <summary>Sort-key selectors for the pantry grid, keyed by the column's <c>SortKey</c>. Each returns
    /// an <see cref="IComparable"/> so a single direction switch covers every column; boxing sorts
    /// identically to a typed <c>OrderBy</c> (nulls first, same comparer).</summary>
    private static readonly Dictionary<string, Func<PantryListItem, IComparable?>> PantrySortKeys = new()
    {
        ["name"]     = i => i.Name,
        ["category"] = i => i.CategoryName,
        ["location"] = i => i.LocationDisplay,
        ["kind"]     = i => i.IsVariant,
        ["qty"]      = i => i.TotalQuantity,
        ["expiry"]   = i => i.SoonestExpiry,
    };

    /// <summary>Applies the requested column sort; unknown or absent keys leave order untouched.
    /// <c>internal</c> so it can be unit-tested directly (no page-handler test harness exists).</summary>
    internal static IEnumerable<PantryListItem> ApplyPantrySort(IEnumerable<PantryListItem> rows, GridSort? sort) =>
        sort is { } s && PantrySortKeys.TryGetValue(s.Key, out var key)
            ? (s.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            : rows;

    private static GridRow BuildPantryRow(PantryListItem item, DateOnly today) => new(
    [
        GridCell.Link(item.Name, $"/Pantry/Products/Detail/{item.ProductId}"),
        GridCell.CategoryChip(item.CategoryName, item.CategoryHue),
        item.LocationDisplay is { } loc ? GridCell.Text(loc) : GridCell.Muted("—"),
        item.IsVariant ? GridCell.Badge("Variant", BadgeTone.Neutral) : GridCell.Muted("—"),
        GridCell.Text($"{item.TotalQuantity:0.###} {item.DisplayUnitCode}"),
        ExpiryCell(item, today),
    ]);

    /// <summary>
    /// The pantry Expiry cell — the hybrid decided in plantry-fdoq. <see cref="ExpiryTone"/> (which already bakes
    /// in the per-household "expiring soon" horizon) decides whether the row is <i>actionable</i>: Expired/Soon
    /// render the unified <c>.badge-expiry</c> pill with relative wording + colour tier from
    /// <see cref="ExpiryDisplay"/> (shared with the Today rail and Recipe rows); a calm Ok row shows just the
    /// muted absolute date; None (no dated lots) shows "—". So pill-presence = within the attention horizon and
    /// pill-colour = urgency — an in-horizon item still several days out shows a calm 'ok'-toned pill by design.
    /// <c>internal</c> so the tone→cell-kind mapping can be unit-tested directly (mirrors <see cref="ApplyPantrySort"/>;
    /// no page-handler test harness exists for this page).
    /// </summary>
    internal static GridCell ExpiryCell(PantryListItem item, DateOnly today)
    {
        if (item.SoonestExpiry is not { } expiry)
            return GridCell.Muted("—"); // ExpiryTone.None — no dated lots

        if (item.ExpiryTone is ExpiryTone.Expired or ExpiryTone.Soon)
        {
            var (label, tier) = ExpiryDisplay.Format(expiry, today);
            return GridCell.ExpiryBadge(label, tier);
        }

        return GridCell.Muted(expiry.ToString("d MMM")); // Ok — beyond the horizon: muted absolute date
    }

    private DataGridViewModel BuildPantryGrid(IReadOnlyList<PantryListItem> items, GridSort? sort = null)
    {
        var today = Today();
        return new(
            Id: "pantry-grid",
            SortUrl: Url.Page("./Index", "SortPantry"),
            CurrentSort: sort,
            Columns:
            [
                new("Name",     SortKey: "name"),
                new("Category", SortKey: "category"),
                new("Location", SortKey: "location"),
                new("Kind",     SortKey: "kind"),
                new("Quantity", GridAlign.End, SortKey: "qty"),
                new("Expiry",   SortKey: "expiry"),
            ],
            Rows: [.. ApplyPantrySort(items, sort).Select(i => BuildPantryRow(i, today))],
            EmptyMessage: "Nothing here yet — add your first item with the Add stock button above.");
    }

    private async Task<IActionResult> ReloadSheetAsync()
    {
        await LoadAddOptionsAsync();
        return Partial("_AddStockSheet", this);
    }

    private async Task<DateOnly?> ResolveExpiryAsync(Guid productId, DateOnly? entered)
    {
        if (entered is not null) return entered;

        var product = await products.FindAsync(ProductId.From(productId));
        if (product is null) return null;

        Category? category = product.CategoryId is { } categoryId ? await categories.FindAsync(categoryId) : null;
        return ExpiryDefaultResolver.ResolveDefaultDueDays(product, category) is { } dueDays
            ? Today().AddDays(dueDays)
            : null;
    }

    private async Task LoadAddOptionsAsync()
    {
        ProductOptions = (await catalog.ListProductsAsync())
            .Where(p => p.CanHoldStock)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToList();

        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();

        LocationOptions = (await locations.ListActiveAsync())
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
    }

    private static IReadOnlyList<PantryListItem> Filter(
        IReadOnlyList<PantryListItem> items, string? q, string? expiry,
        string? category, string? location, bool hideVariants)
    {
        IEnumerable<PantryListItem> rows = items;
        if (!string.IsNullOrWhiteSpace(q))
            rows = rows.Where(i => i.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
        rows = expiry switch
        {
            "soon"    => rows.Where(i => i.ExpiryTone is ExpiryTone.Soon),
            "expired" => rows.Where(i => i.ExpiryTone is ExpiryTone.Expired),
            _         => rows,
        };
        if (!string.IsNullOrWhiteSpace(category))
            rows = rows.Where(i => i.CategoryName == category);
        if (!string.IsNullOrWhiteSpace(location))
            rows = rows.Where(i => i.LocationDisplay == location);
        if (hideVariants)
            rows = rows.Where(i => !i.IsVariant);
        return rows.ToList();
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

/// <summary>View model for the pantry list fragment; <see cref="Oob"/> drives an htmx out-of-band swap
/// after a successful add (so the sheet response refreshes the list without re-rendering the page).</summary>
public sealed record PantryListPartialModel(DataGridViewModel Grid, bool Oob);
