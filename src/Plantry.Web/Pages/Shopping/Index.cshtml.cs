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

    /// <summary>Active categories for the recategorize dropdown (plantry-259).</summary>
    public IReadOnlyList<ShoppingCategoryOption> CategoryOptions { get; private set; } = [];

    /// <summary>All household units for the inline qty editor unit select (plantry-259).</summary>
    public IReadOnlyList<ShoppingUnitOption> UnitOptionsList { get; private set; } = [];

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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: true));
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
    }

    /// <summary>
    /// POST: edits the quantity and unit of a single item (plantry-dem, inline qty/unit editor).
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostEditQuantityAsync(Guid listId, Guid itemId, decimal? quantity, Guid? unitId)
    {
        var cmd = new EditQuantityCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            quantity: quantity,
            unitId: unitId,
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
    }

    /// <summary>
    /// POST: sets or clears the note on a single item (plantry-dem, inline note editor).
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostSetNoteAsync(Guid listId, Guid itemId, string? note)
    {
        var cmd = new SetNoteCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            note: note,
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
    }

    /// <summary>
    /// POST: assigns a category to a single uncategorized item (recategorize action, plantry-259).
    /// After assignment the query service re-groups the list, moving the item into the named category.
    /// Returns the updated list fragment.
    /// </summary>
    public async Task<IActionResult> OnPostRecategorizeAsync(Guid listId, Guid itemId, Guid? categoryId)
    {
        var cmd = new SetCategoryCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            categoryId: categoryId,
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
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
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ReloadPageAsync()
    {
        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        return Partial("_ShoppingList", new ShoppingListPartialModel(ShoppingList, ProductOptions, UnitOptions, CategoryOptions, UnitOptionsList, Oob: false));
    }

    private async Task LoadAddOptionsAsync()
    {
        var candidates = await catalog.ListProductsAsync();
        ProductOptions = candidates
            .Select(p => new SelectListItem(p.Name, p.ProductId.ToString()))
            .ToList();

        // Unit options: sourced from the household's catalog unit table via ListUnitsAsync (plantry-259).
        // The "no unit" option (empty value) is always prepended so users can add items without a unit.
        var unitOptions = await catalog.ListUnitsAsync();
        UnitOptionsList = unitOptions;
        UnitOptions = unitOptions
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.UnitId.ToString()))
            .Prepend(new SelectListItem("no unit", ""))
            .ToList();

        // Category options: sourced from the household's active catalog categories (plantry-259).
        // Used to populate the recategorize dropdown on uncategorized items.
        CategoryOptions = await catalog.ListCategoriesAsync();
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
    IReadOnlyList<ShoppingCategoryOption> CategoryOptions,
    IReadOnlyList<ShoppingUnitOption> UnitOptionsList,
    bool Oob);

/// <summary>
/// View model passed to the <c>_ShoppingItem</c> partial — combines the item view with the
/// category options list (for the recategorize dropdown) and unit options list (for the
/// inline qty editor unit select), both populated server-side from the catalog (plantry-259).
/// </summary>
public sealed record ShoppingItemPartialModel(
    ShoppingListItemView Item,
    IReadOnlyList<ShoppingCategoryOption> CategoryOptions,
    IReadOnlyList<ShoppingUnitOption> UnitOptions);
