namespace Plantry.Web.Pages.Shared;

/// <summary>
/// One row of the `_SortableList` partial — a draggable `.catalog-list__item` carrying the id and
/// sort order the drag-and-drop interaction (see wwwroot/js/sortable-list.js) needs to track and
/// persist a new order.
/// </summary>
public sealed record SortableListItem(string Id, string Primary, string? Secondary, int SortOrder);

/// <summary>htmx endpoint the list posts the dragged order to — see `_SortableList` for the expected request shape (a repeated `ids` form field, one per item, in the new order).</summary>
public sealed record SortableListViewModel(IReadOnlyList<SortableListItem> Items, string ReorderUrl, string EmptyMessage);
