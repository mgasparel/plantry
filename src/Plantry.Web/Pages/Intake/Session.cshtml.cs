using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Domain;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Domain;
using Plantry.MealPlanning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Intake;

/// <summary>One receipt line rendered on the session detail's line grid (receipt-intake-history.md H8).</summary>
/// <param name="ProductHref">
/// Null for a dismissed line, or one whose product no longer resolves — the view then renders
/// <paramref name="ProductLabel"/> as plain text rather than a link (H10).
/// </param>
public sealed record SessionLineView(
    Guid ImportLineId,
    int LineNo,
    string ReceiptText,
    string ProductLabel,
    string? ProductHref,
    bool IsNewProduct,
    bool HasEachEstimate,
    string QuantityDisplay,
    string PriceDisplay,
    bool IsDismissed);

/// <summary>
/// SPEC/receipt-intake-history.md H7 — the read-only detail view of one committed intake at
/// <c>/Intake/Session/{id}</c>. State guard: a <c>Ready</c> session redirects to Review (the only surface
/// for a live session); any other non-Committed status (Failed/Discarded/Parsing) redirects to History
/// (nothing to show); a foreign/unknown id 404s.
/// </summary>
[Authorize]
public sealed class SessionModel(
    IImportSessionRepository sessions,
    IProductRepository products,
    IUnitRepository units,
    IProductStockRepository stocks,
    IHouseholdMemberReader members,
    ITenantContext tenant,
    DisplayCurrencyAccessor displayCurrency) : PageModel
{
    public CommittedSessionDetail Detail { get; private set; } = null!;
    public IReadOnlyList<SessionLineView> Lines { get; private set; } = [];
    public string DisplayCurrency { get; private set; } = "USD";
    public string ScannedByName { get; private set; } = "someone";

    /// <summary>Committed (non-dismissed) line count — the receipt-stats strip's "Items added".</summary>
    public int ItemsAdded => Detail.Lines.Count(l => !l.IsDismissed);

    /// <summary>Sum of committed line prices — the receipt-stats strip's "Stocked value".</summary>
    public decimal StockedValue => Detail.Lines.Where(l => !l.IsDismissed).Sum(l => l.Price ?? 0m);

    public string FormatMoney(decimal amount) => MoneyDisplay.Format(amount, DisplayCurrency);

    /// <summary>
    /// "committed same day" / "committed N days later" / "" when either timestamp is missing — the
    /// page-header subtitle's commit-recency clause (H8).
    /// </summary>
    public string CommitRecencyText()
    {
        if (Detail.CommittedAt is not { } committedAt)
            return string.Empty;

        var purchaseDate = Detail.PurchaseDate ?? DateOnly.FromDateTime(Detail.CreatedAt.LocalDateTime);
        var committedDate = DateOnly.FromDateTime(committedAt.LocalDateTime);
        var days = committedDate.DayNumber - purchaseDate.DayNumber;
        return days switch
        {
            <= 0 => "committed same day",
            1 => "committed 1 day later",
            _ => $"committed {days} days later",
        };
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (tenant.HouseholdId is not { } hid)
            return Forbid();

        var sessionId = ImportSessionId.From(id);
        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null)
            return NotFound();

        // State guard (H7): Ready is a live session — Review remains its sole surface. Any other
        // non-Committed status has no finished detail to show, so it lands back on the history log.
        if (session.Status == ImportStatus.Ready)
            return RedirectToPage("/Intake/Review", new { id });
        if (session.Status != ImportStatus.Committed)
            return RedirectToPage("/Intake/History");

        var result = await new GetCommittedSessionDetailQuery(sessionId, sessions, tenant).ExecuteAsync(ct);
        if (result.IsFailure)
            return NotFound(); // defensive — the guard above already establishes Committed + household match

        Detail = result.Value;
        DisplayCurrency = await displayCurrency.GetAsync(ct);

        // Sequential, not Task.WhenAll: every lookup below shares the same scoped EF DbContext-backed
        // repositories (products/units/stocks), which cannot run concurrent operations.
        var householdId = HouseholdId.From(hid);
        var lines = new List<SessionLineView>(Detail.Lines.Count);
        foreach (var line in Detail.Lines)
            lines.Add(await BuildLineViewAsync(line, householdId, ct));
        Lines = lines;

        var member = (await members.ListMembersAsync(ct)).FirstOrDefault(m => m.UserId == Detail.UserId);
        ScannedByName = member?.DisplayName ?? "someone";

        return Page();
    }

    private async Task<SessionLineView> BuildLineViewAsync(CommittedLineRow line, HouseholdId householdId, CancellationToken ct)
    {
        if (line.IsDismissed)
        {
            return new SessionLineView(
                line.ImportLineId, line.LineNo, line.ReceiptText,
                ProductLabel: "Dismissed during review", ProductHref: null,
                IsNewProduct: false, HasEachEstimate: false,
                QuantityDisplay: "—",
                PriceDisplay: line.Price is { } p ? MoneyDisplay.Format(p, DisplayCurrency) : "—",
                IsDismissed: true);
        }

        var (productLabel, productHref) = await ResolveProductAsync(line.ProductId ?? line.CreatedProductId, householdId, ct);
        var quantityDisplay = line is { Quantity: { } qty, UnitId: { } unitId }
            ? $"{qty:0.###} {await ResolveUnitCodeAsync(unitId, ct)}"
            : "—";

        return new SessionLineView(
            line.ImportLineId, line.LineNo, line.ReceiptText,
            productLabel, productHref,
            IsNewProduct: line.CreatedProductId.HasValue,
            HasEachEstimate: line.HasEachEstimate,
            QuantityDisplay: quantityDisplay,
            PriceDisplay: line.Price is { } price ? MoneyDisplay.Format(price, DisplayCurrency) : "—",
            IsDismissed: false);
    }

    /// <summary>
    /// Product link resolution (H10, mirrors plantry-kkeg's xlink fallback semantics): pantry product
    /// detail when the product holds a stock record, catalog product detail otherwise; a product removed
    /// from the catalog since commit renders as plain, unlinked text.
    /// </summary>
    private async Task<(string Label, string? Href)> ResolveProductAsync(Guid? productId, HouseholdId householdId, CancellationToken ct)
    {
        if (productId is not { } pid)
            return ("(product removed)", null);

        var product = await products.FindAsync(ProductId.From(pid), ct);
        if (product is null)
            return ("(product removed)", null);

        var hasStock = await stocks.FindAsync(householdId, pid, ct) is not null;
        var href = hasStock
            ? Url.Page("/Pantry/Products/Detail", new { id = pid })
            : Url.Page("/Catalog/Products/Detail", new { id = pid });
        return (product.Name, href);
    }

    private readonly Dictionary<Guid, string> _unitCodeCache = [];

    private async Task<string> ResolveUnitCodeAsync(Guid unitId, CancellationToken ct)
    {
        if (_unitCodeCache.TryGetValue(unitId, out var cached))
            return cached;

        var unit = await units.FindAsync(UnitId.From(unitId), ct);
        var code = unit?.Code ?? "?";
        _unitCodeCache[unitId] = code;
        return code;
    }
}
