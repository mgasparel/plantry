namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View-model for the shared ingredient "Section (optional)" picker partial
/// (<c>Shared/_IngredientSectionPicker</c>).
///
/// <para>The picker is the single entry point for assigning a recipe ingredient to a named section
/// ("For the sauce", "For the dough"). Extracted from <c>_ProductSearchCreateSheet</c> (plantry-vff8)
/// so it can render in BOTH sheet views (plantry-7im4): the search view (once a product is picked or a
/// staple is being authored) AND the create view (an inline-created ingredient, which previously had no
/// section field at all and so could never be grouped).</para>
///
/// <para>The markup is view-agnostic: its inner Alpine scope writes only <c>draft.groupHeading</c> and
/// reads the host page's <c>existingSectionHeadings()</c> (Recipes/Edit x-data). It is rendered only by
/// hosts that opt into <c>ShowGroupHeading</c> (Recipes); Take Stock and Deals gate it out entirely, so
/// their sheets stay byte-identical.</para>
/// </summary>
public sealed class IngredientSectionPickerViewModel
{
    /// <summary>
    /// <c>id</c> of the picker's text input and the matching <c>for</c> on its label. MUST be unique per
    /// rendered instance — the search and create views each render one, and both live in the DOM at once
    /// (the sheet swaps views via <c>x-show</c>, not <c>x-if</c>), so a shared id would collide.
    /// The search view passes <c>"ingredient-section"</c>; the create view <c>"ingredient-section-create"</c>.
    /// </summary>
    public required string FieldId { get; init; }

    /// <summary>
    /// Alpine <c>x-show</c> guard for the field. The search view passes
    /// <c>"draft.productId || draft.newStapleName !== null"</c> (show once a line is being authored);
    /// the create view passes <c>"draft.newStapleName !== null"</c> (always true while the create view is
    /// active, since the create view exists only for an inline-create draft).
    /// </summary>
    public required string ShowExpression { get; init; }
}
