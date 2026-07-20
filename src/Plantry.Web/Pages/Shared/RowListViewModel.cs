namespace Plantry.Web.Pages.Shared;

/// <summary>
/// One row of the shared <c>_RowList</c> partial — the primary/secondary/meta line pattern
/// repeated across the catalog CRUD-list pages (Catalog/Index, Products/Index, Locations,
/// Categories) and the lists inside Product Detail.
/// <para>
/// <see cref="Id"/> carries the entity id that the optional drag-reorder
/// (<c>data-id</c>/<c>data-sort-order</c>) and the optional Delete form both key off; leave it
/// null for a static, non-deletable row. <see cref="SortOrder"/> only matters when the owning
/// list is reorderable — pass rows already in the order you want them rendered.
/// </para>
/// </summary>
public sealed record RowListItem(
    string Primary,
    string? Href = null,
    string? Secondary = null,
    string? Meta = null,
    string? Id = null,
    int SortOrder = 0);

/// <summary>
/// The single row-list primitive (merges the former <c>_IndexList</c> + <c>_SortableList</c>).
/// Renders a <c>.catalog-list</c> of primary/secondary/meta rows, or an empty state.
/// <list type="bullet">
/// <item><description>Set <see cref="Reorderable"/> (with a <see cref="ReorderUrl"/>) to make rows
/// drag-reorderable via <c>[data-sortable-list]</c> — the drag interaction and the POST-on-drop of
/// the new id order are owned by <c>wwwroot/js/site.js</c>.</description></item>
/// <item><description>Set <see cref="Deletable"/> to render a per-row Remove form that posts to the
/// hosting page's <c>Delete</c> handler with the row's <see cref="RowListItem.Id"/>.</description></item>
/// </list>
/// </summary>
public sealed record RowListViewModel(
    IReadOnlyList<RowListItem> Items,
    string EmptyMessage,
    bool Reorderable = false,
    string? ReorderUrl = null,
    bool Deletable = false);
