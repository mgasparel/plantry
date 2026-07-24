namespace Plantry.Web.Pages.Shared;

/// <summary>Horizontal alignment of a column header and the cells beneath it.</summary>
public enum GridAlign { Start, Center, End }

/// <summary>The closed vocabulary of cell presentations the `_DataGrid` partial knows how to render.</summary>
public enum GridCellKind { Text, Muted, Link, Badge, Actions, ExpiryBadge, SourceChip }

/// <summary>The icon a <see cref="GridCellKind.SourceChip"/> cell renders (receipt-intake-history.md H11)
/// — which cross-context surface the chip links to.</summary>
public enum SourceChipIcon { Receipt, Cook }

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
    bool IsHxGet = false, string? HxTarget = null, string? HxSwap = null,
    bool IsIcon = false, string? IconId = null,
    bool IsBadge = false, BadgeTone BadgeTone = BadgeTone.Neutral)
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

    /// <summary>
    /// An icon-only navigation action (plantry-sjfn) — a quiet ghost icon button rather than the
    /// text ghost-button treatment the other factories produce. <paramref name="label"/> is not
    /// rendered as text; it becomes the button's <c>aria-label</c>/<c>title</c> so the affordance
    /// stays accessible and hoverable. <paramref name="iconId"/> names a <c>&lt;symbol&gt;</c> id
    /// already registered in <c>_Layout.cshtml</c>'s icon sprite (e.g. <c>"i-edit"</c>).
    /// </summary>
    public static GridAction Icon(string label, string url, string iconId) =>
        new(label, url, IsIcon: true, IconId: iconId);

    /// <summary>
    /// A mutating htmx POST rendered as a tappable <c>.badge</c> pill rather than a ghost button
    /// (plantry-1le6's "Open" badge, tapped again to un-mark) — the same tonal palette
    /// <see cref="GridCell.Badge"/> cells use, so a row's status badge and its own undo control read
    /// identically. Redirect-driven handlers (an <c>HX-Redirect</c> response, e.g. the toast-carrying
    /// full-page PRG) make the swap target/style moot, but a target/swap are still supplied for any
    /// handler that instead swaps a fragment.
    /// </summary>
    public static GridAction PostBadge(string label, string url, BadgeTone tone = BadgeTone.Neutral, string? hxTarget = null, string hxSwap = "outerHTML") =>
        new(label, url, IsPost: true, IsBadge: true, BadgeTone: tone, HxTarget: hxTarget, HxSwap: hxSwap);
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

    /// <summary>The <c>.badge-expiry--{tier}</c> modifier ("urgent"/"soon"/"ok") for an ExpiryBadge cell.</summary>
    public string? ExpiryTier { get; init; }

    /// <summary>The icon for a <see cref="GridCellKind.SourceChip"/> cell.</summary>
    public SourceChipIcon? ChipIcon { get; init; }

    public static GridCell Text(string value) => new() { Kind = GridCellKind.Text, Value = value };
    public static GridCell Muted(string value) => new() { Kind = GridCellKind.Muted, Value = value };
    public static GridCell Link(string value, string url) => new() { Kind = GridCellKind.Link, Value = value, Url = url };
    public static GridCell Badge(string value, BadgeTone tone) => new() { Kind = GridCellKind.Badge, Value = value, Tone = tone };
    public static GridCell Actions(params GridAction[] actions) => new() { Kind = GridCellKind.Actions, Items = actions };

    /// <summary>
    /// The pantry-history provenance chip (receipt-intake-history.md H11) — a resolvable cross-context
    /// source link (Intake receipt line / Cook recipe). <paramref name="url"/> is always non-null: an
    /// unresolved source never becomes a chip at all — the caller falls back to <see cref="Text"/> or
    /// <see cref="Muted"/> instead (the chip is progressive enhancement, never a dead link).
    /// </summary>
    public static GridCell SourceChip(SourceChipIcon icon, string value, string url) =>
        new() { Kind = GridCellKind.SourceChip, Value = value, Url = url, ChipIcon = icon };

    /// <summary>
    /// The unified expiry pill — the canonical <c>.badge-expiry</c> component driven by
    /// <see cref="ExpiryDisplay"/> (plantry-fdoq). Distinct from <see cref="Badge"/> (which renders a generic
    /// <c>.badge--{tone}</c>): only this kind produces the shared expiry look, so the Pantry grid reads the same
    /// as the Today rail and Recipe rows. <paramref name="tierModifier"/> is one of "urgent"/"soon"/"ok".
    /// </summary>
    public static GridCell ExpiryBadge(string label, string tierModifier) =>
        new() { Kind = GridCellKind.ExpiryBadge, Value = label, ExpiryTier = tierModifier };
}

/// <summary>
/// One row — a list of cells the page has already mapped, positionally aligned with
/// <see cref="DataGridViewModel.Columns"/>. <see cref="CssClass"/> is an optional row-level hook
/// (plantry-sjfn) for a whole-row visual treatment the cell vocabulary can't express by itself —
/// e.g. the Pantry "Everything" scope's quiet styling for a never-stocked row
/// (<c>"data-grid__row--muted"</c>) — without inventing a bespoke per-page table.
/// </summary>
public sealed record GridRow(IReadOnlyList<GridCell> Cells, string? CssClass = null);

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
