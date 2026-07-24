using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Web.Pages.Shared;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Shopping;

[Authorize]
public sealed class IndexModel(
    ShoppingListQueryService queryService,
    PantrySuggestionService suggestionService,
    IShoppingCatalogReader catalog,
    IShoppingPantryReader pantry,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddItemCommand> addItemLogger,
    ILogger<CheckOffCommand> checkOffLogger,
    ILogger<UncheckItemCommand> uncheckLogger,
    ILogger<DeleteItemCommand> deleteLogger,
    ILogger<EditQuantityCommand> editQuantityLogger,
    ILogger<SetNoteCommand> setNoteLogger,
    ILogger<SetCategoryCommand> setCategoryLogger,
    ILogger<ClearCheckedCommand> clearCheckedLogger) : PageModel
{
    // ── View state ────────────────────────────────────────────────────────────

    public ShoppingListView? ShoppingList { get; private set; }

    public IReadOnlyList<SelectListItem> ProductOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>Active categories for the recategorize dropdown (plantry-259).</summary>
    public IReadOnlyList<ShoppingCategoryOption> CategoryOptions { get; private set; } = [];

    /// <summary>All household units for the inline qty editor unit select (plantry-259).</summary>
    public IReadOnlyList<ShoppingUnitOption> UnitOptionsList { get; private set; } = [];

    /// <summary>
    /// Pantry suggestions for the "Running low in your pantry" strip (plantry-48l / plantry-14q).
    /// Populated only on the initial GET and the 3 affecting POST handlers. Empty on all other
    /// handlers — those do not re-render the strip.
    /// </summary>
    public IReadOnlyList<PantrySuggestion> Suggestions { get; private set; } = [];

    /// <summary>
    /// True when the suggestions strip should be re-rendered as an OOB swap in the POST response.
    /// Set only by the 3 affecting handlers: product-backed AddItem, DeleteItem (product-backed),
    /// and ClearChecked. Non-affecting handlers leave this false and omit the OOB fragment.
    /// </summary>
    public bool SuggestionsOob { get; private set; }

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
        Suggestions = await LoadSuggestionsAsync(ShoppingList);
    }

    /// <summary>
    /// htmx product search handler for the searchable-select on the add form (plantry-gzro.3 —
    /// migrated onto the shared fuzzy-ranked search component, <c>AllowCreate</c> left at its
    /// default false since Shopping's escape hatch is the existing free-text "Custom item" field,
    /// not a catalog create).
    ///
    /// <para>Returns matching product options as HTML option elements, enriched with pantry stock
    /// hints (.ostock) via the <see cref="IShoppingPantryReader"/> port. This handler renders its
    /// own &lt;li&gt; markup (rather than the tag helper's default <c>AppendOptions</c>) so it can
    /// append that stock hint per item — the "host owns per-item enrichment, component owns chrome"
    /// pattern documented on <see cref="Plantry.Web.TagHelpers.SearchableSelectTagHelper"/>.</para>
    ///
    /// <para>A blank query returns up to 20 unranked products (browse-all on focus, unchanged from
    /// the prior plain-substring behaviour) with no <c>.rk</c> label. A non-blank query is ranked via
    /// <see cref="ProductNameMatcher"/> — the same best/N% vocabulary Recipes/TakeStock's product
    /// search and Intake's AlternativesStrip family already use — and only hits above the display
    /// cutoff are returned.</para>
    /// </summary>
    public async Task<ContentResult> OnGetFilterProductsAsync(string? q)
    {
        var candidates = await catalog.ListProductsAsync();

        List<(Guid ProductId, string Name, string? RankLabel)> matches;
        if (string.IsNullOrWhiteSpace(q))
        {
            matches = candidates
                .Take(20)
                .Select(p => (p.ProductId, p.Name, (string?)null))
                .ToList();
        }
        else
        {
            var ranked = ProductNameMatcher.Rank(
                candidates.Select(p => (p.ProductId, p.Name)),
                q.Trim());
            matches = ranked
                .Take(20)
                .Select((m, i) => (m.Id, m.Name, (string?)ProductNameMatcher.RankLabel(m.Score, isTopHit: i == 0)))
                .ToList();
        }

        // Enrich with stock levels for all matching products in one batch call.
        var productIds = matches.Select(m => m.ProductId).ToList();
        var stockLevels = productIds.Count > 0
            ? await pantry.GetStockLevelsAsync(productIds)
            : (IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>)new Dictionary<Guid, ShoppingPantryStockLevel>();

        var enc = HtmlEncoder.Default;
        var html = new StringBuilder();
        foreach (var match in matches)
        {
            // Shopping-specific trailing enrichment: the pantry-stock badge that sits after the
            // .rk span inside the option. Built here (host owns per-item enrichment) and handed to
            // the shared renderer as already-encoded trailing HTML.
            string? stockBadge = null;
            if (stockLevels.TryGetValue(match.ProductId, out var stock))
            {
                if (stock.OnHand > 0)
                {
                    var lowClass = stock.IsLow ? " low" : "";
                    stockBadge = $"""<span class="ostock{lowClass}">{enc.Encode(stock.OnHand.ToString("0.###"))} {enc.Encode(stock.UnitCode)} in pantry</span>""";
                }
                else
                {
                    stockBadge = """<span class="ostock out">out</span>""";
                }
            }
            html.Append(ProductSearchOptionRenderer.RenderSelectOption(
                match.ProductId.ToString(), match.Name, match.RankLabel, stockBadge));
        }
        return Content(html.ToString(), "text/html");
    }

    /// <summary>
    /// POST: adds an item (product-backed or free-text) to the list (SPEC §3b).
    /// On success, htmx-swaps the full list fragment so the new item appears in the correct group.
    /// Product-backed adds also OOB-swap #pantry-suggestions (the new product may have been a
    /// suggestion; free-text adds do not affect onListProductIds for product-backed suggestions).
    /// </summary>
    public async Task<IActionResult> OnPostAddItemAsync()
    {
        var hasProduct = Input.ProductId.HasValue;
        var hasFreeText = !string.IsNullOrWhiteSpace(Input.FreeText);

        // Neither provided → surface a validation error back into the add form (plantry-3dh.1 C).
        if (!hasProduct && !hasFreeText)
        {
            ModelState.AddModelError(string.Empty, "Choose a product or enter an item name.");
            return await RenderAddResultAsync(updateSuggestions: false);
        }

        // A product selection wins over any leftover custom-item text, so picking a product
        // always succeeds regardless of stale free-text in the box (plantry-3dh.1 B).
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
                tenant: tenant,
                logger: addItemLogger)
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
                tenant: tenant,
                logger: addItemLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await RenderAddResultAsync(updateSuggestions: false);
        }

        // Success → reset the form so the out-of-band swap renders it cleared (plantry-3dh.1 A).
        // ModelState must be cleared too, or the tag helpers re-bind the submitted values.
        ModelState.Clear();
        Input = new AddItemInputModel();

        // Product-backed add changes onListProductIds → refresh the suggestions strip.
        // Free-text add does not add a product-backed item, so the strip is unchanged.
        return await RenderAddResultAsync(updateSuggestions: hasProduct);
    }

    /// <summary>
    /// Renders the AddItem POST response: the refreshed list fragment (main swap) plus the
    /// add form as an out-of-band swap. When <paramref name="updateSuggestions"/> is true,
    /// also appends the suggestions partial as a second OOB fragment (product-backed add path).
    /// On success the form is cleared; on failure it carries the validation summary (plantry-3dh.1 A/C).
    /// </summary>
    private async Task<IActionResult> RenderAddResultAsync(bool updateSuggestions)
    {
        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        if (updateSuggestions)
        {
            Suggestions = await LoadSuggestionsAsync(ShoppingList);
            SuggestionsOob = true;
        }
        return Partial("_AddPostResult", this);
    }

    /// <summary>
    /// POST: checks off one item (SPEC §3c).
    /// Accepts the item id and the list id from the form.
    /// Returns the updated list fragment plus OOB-swaps #sl-summary so header totals
    /// update live (plantry-3dh.2 A).
    /// </summary>
    public async Task<IActionResult> OnPostCheckOffAsync(Guid listId, Guid itemId)
    {
        var cmd = new CheckOffCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            userId: CurrentUserId,
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: checkOffLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            // Unauthorized; return 403.
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // CheckOff does not change onListProductIds — do not recompute or re-render suggestions.
        // OOB-swap the summary so checked/to-buy counts refresh (plantry-3dh.2 A).
        return Partial("_ShoppingListWithSummary", this);
    }

    /// <summary>
    /// POST: unchecks a previously checked item (inverse of CheckOff).
    /// Returns the updated list fragment only — UncheckItem does not change onListProductIds.
    /// </summary>
    public async Task<IActionResult> OnPostUncheckItemAsync(Guid listId, Guid itemId)
    {
        var cmd = new UncheckItemCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: uncheckLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // UncheckItem does not change onListProductIds — do not recompute or re-render suggestions.
        // OOB-swap the summary so checked/to-buy counts refresh (plantry-3dh.2 A).
        return Partial("_ShoppingListWithSummary", this);
    }

    /// <summary>
    /// POST: hard-deletes a single item from the list.
    /// Returns the updated list fragment plus OOB-swaps #pantry-suggestions.
    /// The OOB swap is always emitted on delete (ticket: emitting unconditionally is acceptable),
    /// which ensures product-backed items removed from the list may now re-appear as suggestions.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteItemAsync(Guid listId, Guid itemId)
    {
        var cmd = new DeleteItemCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: deleteLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // DeleteItem changes onListProductIds (for product-backed items) → refresh suggestions via OOB swap.
        Suggestions = await LoadSuggestionsAsync(ShoppingList);
        SuggestionsOob = true;
        return Partial("_DeleteItemResult", this);
    }

    /// <summary>
    /// POST: edits the quantity and unit of a single item (plantry-dem, inline qty/unit editor).
    /// Returns the updated list fragment only — EditQuantity does not change onListProductIds.
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
            tenant: tenant,
            logger: editQuantityLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // EditQuantity does not change onListProductIds — do not recompute or re-render suggestions.
        // OOB-swap the summary to keep totals current (plantry-3dh.2 A).
        return Partial("_ShoppingListWithSummary", this);
    }

    /// <summary>
    /// POST: sets or clears the note on a single item (plantry-dem, inline note editor).
    /// Returns the updated list fragment only — SetNote does not change onListProductIds.
    /// </summary>
    public async Task<IActionResult> OnPostSetNoteAsync(Guid listId, Guid itemId, string? note)
    {
        var cmd = new SetNoteCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            note: note,
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: setNoteLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // SetNote does not change onListProductIds — do not recompute or re-render suggestions.
        // OOB-swap the summary to keep totals current (plantry-3dh.2 A).
        return Partial("_ShoppingListWithSummary", this);
    }

    /// <summary>
    /// POST: assigns a category to a single uncategorized item (recategorize action, plantry-259).
    /// After assignment the query service re-groups the list, moving the item into the named category.
    /// Returns the updated list fragment only — Recategorize does not change onListProductIds.
    /// </summary>
    public async Task<IActionResult> OnPostRecategorizeAsync(Guid listId, Guid itemId, Guid? categoryId)
    {
        var cmd = new SetCategoryCommand(
            listId: ShoppingListId.From(listId),
            itemId: ShoppingListItemId.From(itemId),
            categoryId: categoryId,
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: setCategoryLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure && result.Error != Plantry.SharedKernel.Error.NotFound)
        {
            return Forbid();
        }

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // Recategorize does not change onListProductIds — do not recompute or re-render suggestions.
        // OOB-swap the summary to keep totals current (plantry-3dh.2 A).
        return Partial("_ShoppingListWithSummary", this);
    }

    /// <summary>
    /// POST: clears all checked items (SPEC §3e).
    /// Returns the updated list fragment plus OOB-swaps #pantry-suggestions (cleared checked items
    /// may have been product-backed, so onListProductIds shrinks).
    /// </summary>
    public async Task<IActionResult> OnPostClearCheckedAsync()
    {
        var cmd = new ClearCheckedCommand(
            repository: repository,
            clock: clock,
            tenant: tenant,
            logger: clearCheckedLogger);

        await cmd.ExecuteAsync();

        ShoppingList = await queryService.GetListAsync();
        await LoadAddOptionsAsync();
        // ClearChecked removes product-backed items → refresh suggestions via OOB swap.
        Suggestions = await LoadSuggestionsAsync(ShoppingList);
        SuggestionsOob = true;
        return Partial("_ClearCheckedResult", this);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task LoadAddOptionsAsync()
    {
        var candidates = await catalog.ListProductsAsync();
        ProductOptions = candidates
            .Select(p => new SelectListItem(p.Name, p.ProductId.ToString()))
            .ToList();

        // Unit options: sourced from the household's catalog unit table via ListUnitsAsync (plantry-259),
        // grouped by dimension (plantry-n9iw). The "no unit" option (empty value, ungrouped) is always
        // prepended so users can add items without a unit.
        var unitOptions = await catalog.ListUnitsAsync();
        UnitOptionsList = unitOptions;
        UnitOptions = UnitSelectListBuilder.Build(
                unitOptions,
                u => u.UnitId.ToString(),
                u => $"{u.Code} — {u.Name}",
                u => DimensionExtensions.Parse(u.Dimension),
                u => u.Code)
            .Prepend(new SelectListItem("no unit", ""))
            .ToList();

        // Category options: sourced from the household's active catalog categories (plantry-259).
        // Used to populate the recategorize dropdown on uncategorized items.
        CategoryOptions = await catalog.ListCategoriesAsync();
    }

    /// <summary>
    /// Builds the "Running low in your pantry" suggestion list (plantry-48l).
    /// Collects product ids already on the list (checked and unchecked), then delegates
    /// to <see cref="PantrySuggestionService"/> for the fetch → exclude → order → cap → enrich pipeline.
    /// Only called from handlers that change onListProductIds (AddItem product-backed, DeleteItem,
    /// ClearChecked) and the initial GET.
    /// </summary>
    public Task<IReadOnlyList<PantrySuggestion>> LoadSuggestionsAsync(
        ShoppingListView? list,
        CancellationToken ct = default)
    {
        // Collect product ids already on the list (unchecked AND checked) so we can exclude them.
        // A checked item is "in progress" (user is buying it), so it should not appear as a suggestion.
        IReadOnlySet<Guid> onListProductIds = list is null
            ? (IReadOnlySet<Guid>)new HashSet<Guid>()
            : list.Groups.SelectMany(g => g.Items)
                .Concat(list.UncategorizedItems)
                .Concat(list.CheckedItems)
                .Where(i => i.ProductId.HasValue)
                .Select(i => i.ProductId!.Value)
                .ToHashSet();

        return suggestionService.GetSuggestionsAsync(onListProductIds, ct);
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

/// <summary>
/// View model passed to <c>_ShoppingList</c> partial.
/// <see cref="Oob"/> drives an htmx out-of-band swap when set — the list fragment carries
/// <c>hx-swap-oob="true"</c> so it can replace the list after a form POST that also updates
/// the form state.
/// Suggestions have been removed from this model (plantry-14q): the strip lives in its own
/// <c>_PantrySuggestions</c> partial / <c>#pantry-suggestions</c> container.
/// </summary>
public sealed record ShoppingListPartialModel(
    ShoppingListView? List,
    IReadOnlyList<SelectListItem> ProductOptions,
    IReadOnlyList<SelectListItem> UnitOptions,
    IReadOnlyList<ShoppingCategoryOption> CategoryOptions,
    IReadOnlyList<ShoppingUnitOption> UnitOptionsList,
    bool Oob);

/// <summary>
/// View model passed to the <c>_PantrySuggestions</c> partial (plantry-14q).
/// <see cref="Oob"/> drives an htmx out-of-band swap of <c>#pantry-suggestions</c> when set.
/// </summary>
public sealed record PantrySuggestionsPartialModel(
    IReadOnlyList<PantrySuggestion> Suggestions,
    bool Oob);

/// <summary>
/// View model passed to the <c>_ShoppingSummary</c> partial (plantry-3dh.2 A).
/// <see cref="Oob"/> drives an htmx out-of-band swap of <c>#sl-summary</c> when set,
/// keeping the header stat box (to buy / checked / total) in sync after every mutation.
/// </summary>
public sealed record ShoppingSummaryPartialModel(
    ShoppingListView? List,
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
