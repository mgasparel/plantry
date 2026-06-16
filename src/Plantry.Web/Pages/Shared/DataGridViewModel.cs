namespace Plantry.Web.Pages.Shared;

/// <summary>Horizontal alignment of a column header and the cells beneath it.</summary>
public enum GridAlign { Start, Center, End }

/// <summary>The closed vocabulary of cell presentations the `_DataGrid` partial knows how to render.</summary>
public enum GridCellKind { Text, Muted, Link, Badge, Actions, CategoryChip }

/// <summary>Tonal badge palette, mapped to the generic `.badge--*` modifiers (design tokens) in plenish.css.</summary>
public enum BadgeTone { Neutral, Info, Success, Warning, Danger }

/// <summary>
/// One column of the grid. A non-null <see cref="SortKey"/> (together with a grid-level
/// <c>SortUrl</c>) turns the header into an htmx sort control; the key is opaque to the grid — the
/// page handler decides what it means and does the actual ordering (the grid never sorts data
/// itself, so no reflection over row types is needed).
/// </summary>
public sealed record GridColumn(string Header, GridAlign Align = GridAlign.Start, string? SortKey = null);

/// <summary>The column the grid is currently ordered by and in which direction — echoed back so headers can render the active caret and <c>aria-sort</c>.</summary>
public sealed record GridSort(string Key, bool Descending);

/// <summary>
/// One action inside an <see cref="GridCellKind.Actions"/> cell. A navigation action
/// (<see cref="IsPost"/> = false) renders as a link; a mutation action (<see cref="IsPost"/> = true)
/// renders as an htmx POST carrying the page antiforgery token (attached globally by
/// <c>wwwroot/js/htmx-antiforgery.js</c>) plus an optional <see cref="Confirm"/> prompt, and when
/// <see cref="RemovesRow"/> is set swaps the closest <c>&lt;tr&gt;</c> out on success so the row
/// disappears without a full reload. Centralizing the wiring here keeps every delete behaving
/// identically rather than re-deriving the risky bits per row.
/// </summary>
public sealed record GridAction(
    string Label, string Url, bool IsPost = false, string? Confirm = null, bool RemovesRow = false,
    bool IsHxGet = false, string? HxTarget = null, string? HxSwap = null)
{
    /// <summary>A plain navigation action (edit, jump-to-detail) — just an anchor.</summary>
    public static GridAction Link(string label, string url) => new(label, url);

    /// <summary>A mutating action (delete) — an htmx POST with token, optional confirm, and optional row-removal swap.</summary>
    public static GridAction Post(string label, string url, string? confirm = null, bool removesRow = false) =>
        new(label, url, IsPost: true, Confirm: confirm, RemovesRow: removesRow);

    /// <summary>An htmx GET that loads content into a target element (e.g. open a slide-in sheet).</summary>
    public static GridAction HxGet(string label, string url, string hxTarget, string hxSwap = "innerHTML") =>
        new(label, url, IsHxGet: true, HxTarget: hxTarget, HxSwap: hxSwap);

    /// <summary>A mutating htmx POST that swaps a specific target element rather than removing the row.</summary>
    public static GridAction PostTo(string label, string url, string hxTarget, string hxSwap = "outerHTML", string? confirm = null) =>
        new(label, url, IsPost: true, Confirm: confirm, HxTarget: hxTarget, HxSwap: hxSwap);
}

/// <summary>
/// One cell, drawn from the closed <see cref="GridCellKind"/> vocabulary and built via the static
/// factories rather than constructed directly, so a page maps each domain object to typed cells
/// without the grid ever reflecting over row types.
/// </summary>
public sealed record GridCell
{
    public required GridCellKind Kind { get; init; }

    /// <summary>The display text for Text/Muted/Link/Badge cells.</summary>
    public string? Value { get; init; }

    /// <summary>The destination for a Link cell.</summary>
    public string? Url { get; init; }

    /// <summary>The tone for a Badge cell.</summary>
    public BadgeTone Tone { get; init; }

    /// <summary>The actions for an Actions cell.</summary>
    public IReadOnlyList<GridAction> Items { get; init; } = [];

    /// <summary>oklch hue (0–359) for a CategoryChip cell. Null renders the neutral "?" chip.</summary>
    public int? Hue { get; init; }

    public static GridCell Text(string value) => new() { Kind = GridCellKind.Text, Value = value };
    public static GridCell Muted(string value) => new() { Kind = GridCellKind.Muted, Value = value };
    public static GridCell Link(string value, string url) => new() { Kind = GridCellKind.Link, Value = value, Url = url };
    public static GridCell Badge(string value, BadgeTone tone) => new() { Kind = GridCellKind.Badge, Value = value, Tone = tone };
    public static GridCell Actions(params GridAction[] actions) => new() { Kind = GridCellKind.Actions, Items = actions };

    /// <summary>
    /// A category colour chip: a small two-letter pill derived from <paramref name="categoryName"/>'s first two
    /// characters, coloured via oklch CSS variables at <paramref name="hue"/> degrees. When <paramref name="hue"/>
    /// is null or <paramref name="categoryName"/> is null a neutral "?" chip is rendered.
    /// </summary>
    public static GridCell CategoryChip(string? categoryName, int? hue) =>
        new() { Kind = GridCellKind.CategoryChip, Value = categoryName, Hue = hue };
}

/// <summary>One row — a list of cells the page has already mapped, positionally aligned with <see cref="DataGridViewModel.Columns"/>.</summary>
public sealed record GridRow(IReadOnlyList<GridCell> Cells);

/// <summary>
/// The `_DataGrid` partial's model — a reusable, reflection-free, hypermedia-only data table. The
/// page supplies typed columns and rows; the partial owns all the generic chrome (table structure,
/// sortable headers, empty state, action wiring). Sorting is in-grid (clickable headers post back to
/// <see cref="SortUrl"/>, which returns this whole grid re-rendered); filtering/search/pagination are
/// deliberately out of scope — they decide *which* rows exist and belong to a page toolbar + handler.
/// </summary>
public sealed record DataGridViewModel(
    IReadOnlyList<GridColumn> Columns,
    IReadOnlyList<GridRow> Rows,
    string EmptyMessage,
    string? Id = null,
    string? SortUrl = null,
    GridSort? CurrentSort = null,
    string? Caption = null);
