using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Shopping;

[Authorize]
public sealed class IndexModel(
    ShoppingListQueryService queryService,
    IShoppingCatalogReader catalog,
    IShoppingPantryReader pantry,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    // ── View state ────────────────────────────────────────────────────────────

    public ShoppingListView? ShoppingList { get; private set; }

    public IReadOnlyList<SelectListItem> ProductOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    // ── Input models ──────────────────────────────────────────────────────────

    [BindProperty]
    public AddItemInputModel Input { get; set; } = new();

    public sealed class AddItemInputModel
    {
        /// <summary>Product-backed add: exactly one of ProductId / FreeText must be set.</summary>
        public Guid? ProductId { get; set; }

        /// <summary>Free-text add: exactly one of ProductId / FreeText must be set.</summary>
        [MaxLength(200)]
        public string? FreeText { get; set; }

        [Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal? Quantity { get; set; }

        public Guid? UnitId { get; set; }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    public async Task OnGetAsync()
    {
        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
    }

    /// <summary>
    /// htmx product search handler for the searchable-select on the add form.
    /// Returns matching product options as HTML option elements, enriched with pantry
    /// stock hints (.ostock) via the <see cref="IShoppingPantryReader"/> port.
    /// </summary>
    public async Task<ContentResult> OnGetFilterProductsAsync(string? q)
    {
        var candidates = await catalog.ListProductsAsync();
        var matches = candidates
            .Where(p => string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        // Enrich with stock levels for all matching products in one batch call.
        var productIds = matches.Select(p => p.ProductId).ToList();
        var stockLevels = productIds.Count > 0
            ? await pantry.GetStockLevelsAsync(productIds)
            : (IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>)new Dictionary<Guid, ShoppingPantryStockLevel>();

        var enc = HtmlEncoder.Default;
        var html = new StringBuilder();
        foreach (var match in matches)
        {
            html.Append($"""<li role="option" data-value="{enc.Encode(match.ProductId.ToString())}" @click="select($el.dataset.value, $el.querySelector('[data-label]')?.dataset.label ?? $el.textContent.trim())">""");
            html.Append($"""<span data-label="{enc.Encode(match.Name)}">{enc.Encode(match.Name)}</span>""");
            if (stockLevels.TryGetValue(match.ProductId, out var stock))
            {
                if (stock.OnHand > 0)
                {
                    var lowClass = stock.IsLow ? " low" : "";
                    html.Append($"""<span class="ostock{lowClass}">{enc.Encode(stock.OnHand.ToString("0.###"))} {enc.Encode(stock.UnitCode)} in pantry</span>""");
                }
                else
                {
                    html.Append("""<span class="ostock out">out</span>""");
                }
            }
            html.Append("</li>");
        }
        return Content(html.ToString(), "text/html");
    }

    /// <summary>
    /// POST: adds an item (product-backed or free-text) to the list (SPEC §3b).
    /// On success, htmx-swaps the full list fragment so the new item appears in the correct group.
    /// </summary>
    public async Task<IActionResult> OnPostAddItemAsync()
    {
        // Exactly one of ProductId / FreeText must be set.
        var hasProduct = Input.ProductId.HasValue;
        var hasFreeText = !string.IsNullOrWhiteSpace(Input.FreeText);

        if (hasProduct == hasFreeText)
        {
            ModelState.AddModelError(string.Empty, hasProduct
                ? "Cannot supply both a product and a free-text name."
                : "Choose a product or enter an item name.");
            return await ReloadPageAsync();
        }

        var cmd = hasProduct
            ? new AddItemCommand(
                productId: Input.ProductId,
                freeText: null,
                quantity: Input.Quantity,
                unitId: Input.UnitId,
                note: null,
                source: ItemSource.Manual,
                sourceRef: null,
                intentionalDuplicate: false,
                repository: repository,
                catalogReader: catalog,
                clock: clock,
                tenant: tenant)
            : new AddItemCommand(
                productId: null,
                freeText: Input.FreeText!.Trim(),
                quantity: Input.Quantity,
                unitId: Input.UnitId,
                note: null,
                source: ItemSource.Manual,
                sourceRef: null,
                intentionalDuplicate: false,
                repository: repository,
                catalogReader: catalog,
                clock: clock,
                tenant: tenant);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadPageAsync();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // Return the full shopping list fragment; hx-swap="outerHTML" on #shopping-list
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: true));
    }

    /// <summary>
    /// POST: checks off one item (SPEC §3c).
    /// Accepts the item id and the list id from the form.
    /// Returns the updated list fragment (ordered: checked items sink to bottom).
    /// </summary>
    public async Task<IActionResult> OnPostCheckOffAsync(Guid listId, Guid itemId)
    {
        var cmd = new CheckOffCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            userId: CurrentUserId,
            repository: repository,
            clock: clock,
            tenant: tenant);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            // Unauthorized; return 403.
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: false));
    }

    /// <summary>
    /// POST: unchecks a previously checked item (inverse of CheckOff).
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostUncheckItemAsync(Guid listId, Guid itemId)
    {
        var cmd = new UncheckItemCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            repository: repository,
            clock: clock,
            tenant: tenant);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: false));
    }

    /// <summary>
    /// POST: hard-deletes a single item from the list.
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteItemAsync(Guid listId, Guid itemId)
    {
        var cmd = new DeleteItemCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            repository: repository,
            clock: clock,
            tenant: tenant);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: false));
    }

    /// <summary>
    /// POST: clears all checked items (SPEC §3e).
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostClearCheckedAsync()
    {
        var cmd = new ClearCheckedCommand(
            repository: repository,
            clock: clock,
            tenant: tenant);

        await cmd.ExecuteAsync();

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: false));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ReloadPageAsync()
    {
        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, Oob: false));
    }

    private async Task LoadAddOptionsAsync()
    {
        var candidates = await catalog.ListProductsAsync();
        ProductOptions = candidates
            .Select(p => new SelectListItem(p.Name, p.ProductId.ToString()))
            .ToList();

        // Unit options: resolved via the catalog reader.
        // For the initial load we populate from the catalog reader's unit list;
        // the add form only needs a basic "no unit" + common options.
        // Units are loaded directly via the query service's catalog reader.
        // We use an empty list here and rely on the searchable-select's server
        // search for products; unit is optional (a select with common units will
        // be populated via the catalog reader — but IShoppingCatalogReader does
        // not expose a ListUnitsAsync; use the unit repository injected indirectly
        // through the catalog reader adapter).
        // Interpretation: units list is populated server-side from the catalog
        // unit repository. Since ShoppingCatalogReaderAdapter holds a reference
        // to IUnitRepository, and the Web page does not directly hold that reference,
        // the simplest correct approach is to expose unit listing via the catalog
        // reader port. For now, units are optional on add and the select shows a
        // "No unit" option — the unit options are populated from whatever the catalog
        // reader can provide. We do NOT add a ListUnitsAsync to the port here to
        // avoid expanding the scope; a deferred bead covers enriching the unit select.
        // The unit field posts an optional Guid? UnitId; if no unit is selected the
        // item is stored without a unit (valid per domain — Quantity and UnitId are
        // both nullable).
        UnitOptions = [];
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

/// <summary>
/// View model passed to <c>_ShoppingList</c> partial.
/// <see cref="Oob"/> drives an htmx out-of-band swap when set — the list fragment carries
/// <c>hx-swap-oob="true"</c> so it can replace the list after a form POST that also updates
/// the form state.
/// </summary>
public sealed record ShoppingListPartialModel(
    ShoppingListView? List,
    IReadOnlyList<SelectListItem> ProductOptions,
    IReadOnlyList<SelectListItem> UnitOptions,
    bool Oob);
