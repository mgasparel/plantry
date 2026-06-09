namespace Plantry.Web.Pages.Shared;

/// <summary>
/// One row of the `_IndexList` partial — the primary/secondary/meta line pattern repeated
/// across Catalog/Index, Products/Index, and the lists inside Product Detail.
/// </summary>
public sealed record IndexListItem(string Primary, string? Href = null, string? Secondary = null, string? Meta = null);

public sealed record IndexListViewModel(IReadOnlyList<IndexListItem> Items, string EmptyMessage);
