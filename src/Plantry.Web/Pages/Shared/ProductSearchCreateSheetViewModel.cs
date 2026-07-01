using Microsoft.AspNetCore.Mvc.Rendering;

namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View-model for the shared product search / create sheet partial
/// (<c>Shared/_ProductSearchCreateSheet</c>).
///
/// <para>The sheet provides a reusable ingredient/item add drawer with two views:</para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Search view (default)</b> — the ONE shared fuzzy-ranked search + create component
///       (plantry-gzro, <c>&lt;searchable-select allow-create="true"&gt;</c>). Search results
///       dispatch a <c>pick-product</c> CustomEvent on selection carrying
///       <c>{ value, name, track }</c>; a divider + demoted/full-strength "+ Create ..." button
///       below the listbox dispatches <c>product-search-create</c> and navigates to the create
///       view in-place (<c>sheetView = 'create'</c>).
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Create view</b> — a full-panel second view of the same flyout (not a nested modal).
///       Swapped in place via <c>x-show</c>; header swaps title + backlink. The host page drives
///       the view via <c>sheetView: 'search' | 'create'</c> in its Alpine <c>x-data</c>.
///       Siblings plantry-40n6 (group/variant) and plantry-y53t (defaults collapsible) extend
///       the create-view body; this scaffold provides the shell.
///     </description>
///   </item>
/// </list>
///
/// <para><b>Event contract:</b></para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>pick-product</c> — dispatched by each search result <c>&lt;li&gt;</c> on click.
///       Detail: <c>{ value: string (ProductId), name: string, track: string ("true"|"false") }</c>.
///       The host page catches this on the sheet element via <c>@@pick-product="selectProduct(draft, $event.detail)"</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>sheet-product-set</c> — dispatched by the host page when opening the sheet to pre-populate
///       the search field (via <c>window.dispatchEvent</c>). The partial listens on
///       <c>@@sheet-product-set.window</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>product-search-create</c> — dispatched by the search component's create button.
///       Detail: <c>{ query: string }</c>. The partial catches this on the <c>.sheet</c> element and
///       sets <c>sheetView = 'create'</c>, seeding <c>draft.newStapleName</c> from the query.
///     </description>
///   </item>
/// </list>
///
/// <para><b>TrackStock</b> controls whether the quantity + unit fields are rendered inside the sheet.
/// For Recipes (ingredient qty matters) pass <c>true</c>. For Take Stock inline-add (untracked; only
/// the product identity and default-location matter) pass <c>false</c>. The host may always inject
/// additional fields via <see cref="ExtraFieldsPartial"/>.</para>
///
/// <para><b>ExtraFieldsPartial</b> is an optional partial name that is rendered between the product
/// picker and the action buttons. Use it for host-specific fields (e.g. the count + location selects
/// that Take Stock's inline-add sheet needs, P4-7). When null no extra markup is emitted.</para>
///
/// <para><b>Noun</b> drives the sheet header and aria-label: "Add {Noun}" / "Edit {Noun}".
/// Defaults to "ingredient" (Recipes). Take Stock passes "item".</para>
///
/// <para><b>ShowGroupHeading</b> controls whether the "Group (optional)" field is rendered.
/// It is a Recipes-specific concept (ingredient sections) and is irrelevant for Take Stock.
/// Defaults to <c>true</c> (Recipes). Take Stock passes <c>false</c>.</para>
///
/// <para><b>CreateLabel</b> is the trailing phrase in the search component's create button, e.g.
/// "+ Create "chicken" {CreateLabel}" (the button that switches from search mode to new-product
/// mode). Defaults to "as a new staple" (Recipes). Take Stock passes "as a new product"
/// because the button actually creates a tracked product in that context.</para>
///
/// <para><b>StapleNamePlaceholder</b> overrides the placeholder text in the staple-name input shown
/// when the user switches to create mode. Defaults to "Staple name (e.g. Salt)" (Recipes).
/// Take Stock passes neutral wording.</para>
/// </summary>
public sealed class ProductSearchCreateSheetViewModel
{
    /// <summary>
    /// htmx endpoint (hx-get) that returns <c>&lt;li role="option"&gt;</c> markup as the user types.
    /// Must accept a <c>q</c> query parameter and return HTML.
    /// </summary>
    public required string SearchUrl { get; init; }

    /// <summary>
    /// Unit options rendered in the Staple-create unit select and (when <see cref="TrackStock"/> is true)
    /// the recipe ingredient unit select inside the sheet.
    /// </summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; init; } = [];

    /// <summary>
    /// Category options for the optional Category select inside the Defaults collapsible in the
    /// create view (plantry-y53t). When empty the Category select renders with no options other than
    /// the "— None —" placeholder, which is still valid (Category is always optional on a new product).
    /// </summary>
    public IReadOnlyList<SelectListItem> CategoryOptions { get; init; } = [];

    /// <summary>
    /// When true, quantity and unit fields are rendered for tracked products. Set false when the host
    /// only needs a product identity (e.g. Take Stock inline-add where the count is entered elsewhere).
    /// Defaults to true (Recipes behaviour).
    /// </summary>
    public bool TrackStock { get; init; } = true;

    /// <summary>
    /// Optional partial name rendered ONCE between the two views and the action bar.
    /// The host page passes any extra Alpine draft state via the surrounding <c>x-data</c> context,
    /// so no extra model is needed. When null the slot is empty.
    /// Rendered once (always in DOM, always visible) to avoid duplicate-id collisions
    /// (e.g. <c>#add-count</c> appearing in both views) and to retain user-entered values
    /// across the search↔create view swap without re-mounting the partial.
    /// Example: <c>"TakeStock/_CountLocationFields"</c> for P4-7.
    /// </summary>
    public string? ExtraFieldsPartial { get; init; }

    /// <summary>
    /// Noun used in the sheet header and aria-label: "Add {Noun}" / "Edit {Noun}".
    /// Defaults to "ingredient" (Recipes behaviour). Take Stock passes "item".
    /// </summary>
    public string Noun { get; init; } = "ingredient";

    /// <summary>
    /// When true, the "Group (optional)" field is rendered. It is a Recipes-only concept
    /// (ingredient sections like Sauce/Topping) and is irrelevant for Take Stock.
    /// Defaults to <c>true</c> (Recipes behaviour). Take Stock passes <c>false</c>.
    /// </summary>
    public bool ShowGroupHeading { get; init; } = true;

    /// <summary>
    /// Trailing phrase in the search component's create button below the listbox, e.g.
    /// "+ Create "chicken" {CreateLabel}" (the button that navigates to the create view in-place
    /// via <c>sheetView = 'create'</c>). Defaults to "as a new staple" (Recipes behaviour).
    /// Take Stock passes "as a new product" because the action creates a tracked product.
    /// </summary>
    public string CreateLabel { get; init; } = "as a new staple";

    /// <summary>
    /// Placeholder text for the name input in the create view.
    /// Defaults to "Staple name (e.g. Salt)" (Recipes behaviour).
    /// Take Stock passes neutral wording to match the relabelled context.
    /// </summary>
    public string StapleNamePlaceholder { get; init; } = "Staple name (e.g. Salt)";

    /// <summary>
    /// Sheet header title displayed while the create view is active (<c>sheetView === 'create'</c>).
    /// Defaults to "New product". Pass a different string to context-label the create view
    /// (e.g. a future Recipes variant once plantry-orix wires the tracked-product create path).
    /// </summary>
    public string CreateViewTitle { get; init; } = "New product";

    /// <summary>
    /// When true, the create-view primary action button reads "Create &amp; count" instead of "Create".
    /// Set to <c>true</c> only for the Take Stock context where the create-view also submits an
    /// opening count. Defaults to <c>false</c> (plain "Create" label for Recipes and other surfaces).
    /// </summary>
    public bool ShowCreateAndCount { get; init; } = false;

    /// <summary>
    /// Existing group products (active, <see cref="Plantry.Catalog.Domain.Product.IsParent"/> = true)
    /// for the household, serialised as <c>[{ id, name }]</c> and embedded in the create-view's
    /// Alpine data so the Group combobox can filter client-side without an extra htmx round-trip.
    ///
    /// <para>Each entry is a <see cref="GroupOption"/> with the group's <see cref="GroupOption.Id"/>
    /// (string form of the <c>ProductId</c>) and <see cref="GroupOption.Name"/>.</para>
    ///
    /// <para>Leave empty (default) when the host does not need group-aware create (e.g. a future
    /// context that does not support grouping). The create view hides the Group field when this list
    /// is null.</para>
    ///
    /// <para>Null (the default) = don't render the group field at all. Empty list = render the field
    /// with no existing groups (only the "create new group" option is shown).</para>
    /// </summary>
    public IReadOnlyList<GroupOption>? GroupOptions { get; init; } = null;
}

/// <summary>
/// A single group (parent product) option for the create-view Group combobox
/// (<see cref="ProductSearchCreateSheetViewModel.GroupOptions"/>).
/// </summary>
/// <param name="Id">String form of the group product's <c>ProductId</c>.</param>
/// <param name="Name">Display name of the group product.</param>
public sealed record GroupOption(string Id, string Name);
