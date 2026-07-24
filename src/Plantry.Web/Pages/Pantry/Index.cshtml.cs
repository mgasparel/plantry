using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
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
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    ProductQueryService catalogProducts,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddStockCommand> addStockLogger) : PageModel
{
    /// <summary>Query-string value for the default scope — today's pantry, unchanged.</summary>
    public const string ScopeInStock = "instock";

    /// <summary>Query-string value for the scope that folds in active never-stocked catalog products (plantry-sjfn).</summary>
    public const string ScopeEverything = "everything";

    /// <summary>The active scope for this request — echoed into the seg-ctrl and threaded through
    /// every filter/sort/add-stock round trip so the user's chosen scope survives htmx swaps.</summary>
    public string Scope { get; private set; } = ScopeInStock;

    private static string NormalizeScope(string? scope) =>
        string.Equals(scope, ScopeEverything, StringComparison.OrdinalIgnoreCase) ? ScopeEverything : ScopeInStock;

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

    public async Task OnGetAsync(string? scope)
    {
        Scope = NormalizeScope(scope);
        var items = await LoadItemsAsync(Scope == ScopeEverything);
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

    public async Task<IActionResult> OnGetFilterAsync(
        string? q, string? expiry, string? category, string? location, bool hideVariants, string? scope)
    {
        Scope = NormalizeScope(scope);
        var all = await LoadItemsAsync(Scope == ScopeEverything);
        var items = Filter(all, q, expiry, category, location, hideVariants);
        return Partial("_PantryList", new PantryListPartialModel(BuildPantryGrid(items), Oob: false));
    }

    public async Task<IActionResult> OnGetSortPantryAsync(string sort, bool desc, string? scope)
    {
        Scope = NormalizeScope(scope);
        var items = await LoadItemsAsync(Scope == ScopeEverything);
        var grid = BuildPantryGrid(items, new GridSort(sort, desc));
        return Partial("Shared/_DataGrid", grid);
    }

    public async Task<IActionResult> OnGetAddSheetAsync(string? scope)
    {
        Scope = NormalizeScope(scope);
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

    public async Task<IActionResult> OnPostAddStockAsync(string? scope)
    {
        Scope = NormalizeScope(scope);
        if (!ModelState.IsValid)
            return await ReloadSheetAsync();

        var expiry = Input.ExpiryDate ?? await catalogProducts.DefaultExpiryDateAsync(Input.ProductId!.Value);

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

        var items = await LoadItemsAsync(Scope == ScopeEverything);
        return Partial("_PantryList", new PantryListPartialModel(BuildPantryGrid(items), Oob: true));
    }

    /// <summary>Sort-key selectors for the pantry grid, keyed by the column's <c>SortKey</c>. Each returns
    /// an <see cref="IComparable"/> so a single direction switch covers every column; boxing sorts
    /// identically to a typed <c>OrderBy</c> (nulls first, same comparer). "qty" is handled separately
    /// in <see cref="ApplyPantrySort"/> — unstocked rows need to trail regardless of direction, which a
    /// single boxed comparable can't express.</summary>
    private static readonly Dictionary<string, Func<PantryListItem, IComparable?>> PantrySortKeys = new()
    {
        ["name"]     = i => i.Name,
        ["category"] = i => i.CategoryName,
        ["location"] = i => i.LocationDisplay,
        ["kind"]     = i => i.IsVariant,
        ["expiry"]   = i => i.SoonestExpiry,
    };

    /// <summary>Applies the requested column sort; unknown or absent keys leave order untouched.
    /// <c>internal</c> so it can be unit-tested directly (no page-handler test harness exists).</summary>
    internal static IEnumerable<PantryListItem> ApplyPantrySort(IEnumerable<PantryListItem> rows, GridSort? sort)
    {
        if (sort is not { } s) return rows;

        if (s.Key == "qty")
        {
            // Unstocked rows (Everything scope, plantry-sjfn) carry no real quantity and always trail
            // the stocked rows regardless of direction — a plain OrderBy/OrderByDescending would
            // otherwise surface them first on an ascending sort, since 0 sorts lowest.
            var stocked = rows.Where(i => i.IsStocked);
            var sortedStocked = s.Descending
                ? stocked.OrderByDescending(i => i.TotalQuantity)
                : stocked.OrderBy(i => i.TotalQuantity);
            var unstocked = rows.Where(i => !i.IsStocked).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
            return sortedStocked.Concat(unstocked);
        }

        return PantrySortKeys.TryGetValue(s.Key, out var key)
            ? (s.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            : rows;
    }

    /// <summary>The Kind badge (plantry-sjfn): a synthesized "Everything" scope row for a catalog
    /// parent renders "Parent" (mirroring the retired Catalog Products grid), a variant renders
    /// "Variant" same as always, anything else is muted. <c>internal</c> for the same reason as
    /// <see cref="ApplyPantrySort"/>.</summary>
    internal static GridCell KindCell(PantryListItem item) => item switch
    {
        { IsParent: true } => GridCell.Badge("Parent", BadgeTone.Info),
        { IsVariant: true } => GridCell.Badge("Variant", BadgeTone.Neutral),
        _ => GridCell.Muted("—"),
    };

    /// <summary>
    /// The Name cell (plantry-lxm2): an archived product's row carries a neutral "Archived" badge
    /// inline after its name and links through to the Catalog product detail page — the only page
    /// with the Unarchive control (Pantry's own detail page has no archive affordance) — so an
    /// archived row is never a dead end (fixes the "unarchive is unreachable" gap). Every other row
    /// keeps linking to the Pantry stock detail page as before. <c>internal</c> for the same reason
    /// as <see cref="KindCell"/>.
    /// </summary>
    internal static GridCell NameCell(PantryListItem item) => item.IsArchived
        ? GridCell.Link(item.Name, $"/Catalog/Products/{item.ProductId}", trailingBadge: "Archived", trailingBadgeTone: BadgeTone.Neutral)
        : GridCell.Link(item.Name, $"/Pantry/Products/Detail/{item.ProductId}");

    private static GridRow BuildPantryRow(PantryListItem item, DateOnly today) => new(
        [
            NameCell(item),
            GridCell.Text(item.CategoryName ?? "—"),
            item.LocationDisplay is { } loc ? GridCell.Text(loc) : GridCell.Muted("—"),
            KindCell(item),
            item.IsStocked
                ? GridCell.Text($"{item.TotalQuantity:0.###} {item.DisplayUnitCode}")
                : GridCell.Muted("Not stocked"),
            ExpiryCell(item, today),
            GridCell.Actions(GridAction.Icon("Edit product details", $"/Catalog/Products/{item.ProductId}", "i-edit")),
        ],
        CssClass: item.IsStocked ? null : "data-grid__row--muted");

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
            SortUrl: Url.Page("./Index", "SortPantry", new { scope = Scope }),
            CurrentSort: sort,
            Columns:
            [
                new("Name",     SortKey: "name"),
                new("Category", SortKey: "category"),
                new("Location", SortKey: "location"),
                new("Kind",     SortKey: "kind"),
                new("Quantity", GridAlign.End, SortKey: "qty"),
                new("Expiry",   SortKey: "expiry"),
                new("", GridAlign.End),
            ],
            Rows: [.. ApplyPantrySort(items, sort).Select(i => BuildPantryRow(i, today))],
            EmptyMessage: "Nothing here yet — add your first item with the Add stock button above.");
    }

    /// <summary>Loads the pantry rows for the active scope — the plain in-stock list, or (Everything)
    /// that list plus every catalog product (active and archived, plantry-lxm2) folded in via
    /// <see cref="MergeEverythingScope"/>.</summary>
    private async Task<IReadOnlyList<PantryListItem>> LoadItemsAsync(bool everything)
    {
        var items = await queries.ListPantryAsync();
        if (!everything) return items;

        var everythingCatalog = await catalogProducts.ListEverythingAsync();
        return MergeEverythingScope(items, everythingCatalog);
    }

    /// <summary>
    /// Merges the Pantry "Everything" scope (plantry-sjfn, extended for archived rows by plantry-lxm2):
    /// every catalog product — active or archived — absent from the in-stock list is folded in as a
    /// synthesized zero-lot row (<see cref="PantryListItem.IsStocked"/> false), so the grid reads as
    /// one list rather than two separate ones. An archived product already carrying stock is NOT
    /// synthesized here — it arrives from <paramref name="inStock"/> already, with its own
    /// <see cref="PantryListItem.IsArchived"/> flag set by <c>InventoryQueryService.ListPantryAsync</c>.
    /// Web-layer read composition only, per the design note — Inventory stays unaware Catalog's full
    /// product list even exists. <c>internal</c> so it's directly unit-testable (no page-handler test
    /// harness exists for this page, mirroring <see cref="ApplyPantrySort"/>).
    /// </summary>
    internal static IReadOnlyList<PantryListItem> MergeEverythingScope(
        IReadOnlyList<PantryListItem> inStock, IReadOnlyList<ProductListItem> everythingCatalog)
    {
        var stockedIds = inStock.Select(i => i.ProductId).ToHashSet();
        var unstocked = everythingCatalog
            .Where(p => !stockedIds.Contains(p.Id.Value))
            .Select(p => new PantryListItem(
                ProductId: p.Id.Value,
                Name: p.Name,
                CategoryName: p.CategoryName,
                LocationDisplay: null,
                IsVariant: p.IsVariant,
                TotalQuantity: 0m,
                DisplayUnitCode: p.DefaultUnitCode,
                LotCount: 0,
                SoonestExpiry: null,
                ExpiryTone: ExpiryTone.None,
                IsStocked: false,
                IsParent: p.IsParent,
                IsArchived: p.IsArchived));

        return [.. inStock, .. unstocked];
    }

    private async Task<IActionResult> ReloadSheetAsync()
    {
        await LoadAddOptionsAsync();
        return Partial("_AddStockSheet", this);
    }

    private async Task LoadAddOptionsAsync()
    {
        ProductOptions = (await catalog.ListProductsAsync())
            .Where(p => p.CanHoldStock)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToList();

        UnitOptions = UnitSelectListBuilder.BuildFromUnits(
            await units.ListAsync(),
            u => u.Id.Value.ToString(),
            u => $"{u.Code} — {u.Name}");

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
