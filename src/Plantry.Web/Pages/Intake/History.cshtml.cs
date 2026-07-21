using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Intake;

/// <summary>One calendar-month group of history rows for the /Intake/History grid (receipt-intake-history.md H5).</summary>
public sealed record IntakeMonthBlock(string Label, IReadOnlyList<IntakeHistoryRow> Rows);

/// <summary>
/// SPEC/receipt-intake-history.md H5 — the browsable log of every receipt intake, regardless of outcome
/// (Committed, Ready/"being reviewed", Failed, Discarded all appear; only the transient Parsing status is
/// excluded). Paged by <see cref="GetIntakeHistoryQuery"/>'s created-at cursor; "Show earlier" fetches the
/// next page via <see cref="OnGetMoreAsync"/> and htmx-appends it.
/// </summary>
[Authorize]
public sealed class HistoryModel(
    IImportSessionRepository sessions,
    ITenantContext tenant,
    DisplayCurrencyAccessor displayCurrency) : PageModel
{
    public string DisplayCurrency { get; private set; } = "USD";
    public IReadOnlyList<IntakeMonthBlock> MonthBlocks { get; private set; } = [];

    /// <summary>Unix-ms cursor for the next "Show earlier" fetch; null when this page reached the end.</summary>
    public long? NextCursor { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        DisplayCurrency = await displayCurrency.GetAsync(ct);
        if (tenant.HouseholdId is not { } hid)
            return;

        var page = await new GetIntakeHistoryQuery(sessions)
            .ExecuteAsync(HouseholdId.From(hid), beforeCreatedAt: null, ct: ct);
        Apply(page);
    }

    /// <summary>htmx "Show earlier" fetch — returns the appended month blocks + the next slot (or none).</summary>
    public async Task<IActionResult> OnGetMoreAsync(long before, CancellationToken ct)
    {
        DisplayCurrency = await displayCurrency.GetAsync(ct);
        if (tenant.HouseholdId is { } hid)
        {
            var beforeCreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(before);
            var page = await new GetIntakeHistoryQuery(sessions)
                .ExecuteAsync(HouseholdId.From(hid), beforeCreatedAt, ct: ct);
            Apply(page);
        }

        return Partial("_HistoryBlocks", this);
    }

    private void Apply(IntakeHistoryPage page)
    {
        MonthBlocks = page.Rows
            .GroupBy(r => new DateOnly(r.Date.Year, r.Date.Month, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => new IntakeMonthBlock(g.Key.ToString("MMMM yyyy", CultureInfo.InvariantCulture), g.ToList()))
            .ToList();
        NextCursor = page.NextCursor?.ToUnixTimeMilliseconds();
    }

    public static string FormatDate(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    public string FormatAmount(decimal amount) => MoneyDisplay.Format(amount, DisplayCurrency);
}
