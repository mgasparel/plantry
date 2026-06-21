using Microsoft.AspNetCore.Mvc.Rendering;

namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View-model for the shared product search / create sheet partial
/// (<c>Shared/_ProductSearchCreateSheet</c>).
///
/// <para>The sheet provides a reusable ingredient/item add drawer with two modes:</para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Search mode (default)</b> — an htmx-driven searchable-select that dispatches a
///       <c>pick-product</c> CustomEvent on selection carrying <c>{ value, name, track }</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Staple-create mode (C12)</b> — when no match is found the user switches to inline
///       name + unit create; the host page reads <c>draft.newStapleName</c> / <c>draft.newStapleUnit</c>.
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
    /// When true, quantity and unit fields are rendered for tracked products. Set false when the host
    /// only needs a product identity (e.g. Take Stock inline-add where the count is entered elsewhere).
    /// Defaults to true (Recipes behaviour).
    /// </summary>
    public bool TrackStock { get; init; } = true;

    /// <summary>
    /// Optional partial name rendered between the product picker block and the sheet's action bar.
    /// The host page passes any extra Alpine draft state via the surrounding <c>x-data</c> context,
    /// so no extra model is needed. When null the slot is empty.
    /// Example: <c>"TakeStock/_CountLocationFields"</c> for P4-7.
    /// </summary>
    public string? ExtraFieldsPartial { get; init; }
}
