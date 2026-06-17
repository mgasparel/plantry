using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;

namespace Plantry.Web.Pages.MealPlan;

[Authorize]
public sealed class IndexModel(MealPlanningDbContext db) : PageModel
{
    public DateOnly WeekStart { get; private set; }
    public DateOnly PrevWeekStart { get; private set; }
    public DateOnly NextWeekStart { get; private set; }
    public DateOnly ThisWeekStart { get; private set; }
    public string WeekLabel { get; private set; } = "";
    public bool HasSlots { get; private set; }

    public List<DayColumn> WeekDays { get; private set; } = [];
    public List<SlotRow> Slots { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? week = null, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        ThisWeekStart = Plantry.MealPlanning.Domain.MealPlan.NormalizeToMonday(today);

        WeekStart = week is not null && DateOnly.TryParse(week, out var parsed)
            ? Plantry.MealPlanning.Domain.MealPlan.NormalizeToMonday(parsed)
            : ThisWeekStart;

        PrevWeekStart = WeekStart.AddDays(-7);
        NextWeekStart = WeekStart.AddDays(7);

        var end = WeekStart.AddDays(6);
        WeekLabel = WeekStart.Month == end.Month
            ? $"{WeekStart:MMM d} – {end:d}, {WeekStart:yyyy}"
            : $"{WeekStart:MMM d} – {end:MMM d}, {WeekStart:yyyy}";

        WeekDays = Enumerable.Range(0, 7)
            .Select(i =>
            {
                var d = WeekStart.AddDays(i);
                return new DayColumn(d.ToString("ddd"), d.ToString("d"), d == today);
            })
            .ToList();

        // Load active slots for this household
        var config = await db.MealSlotConfigs
            .Include(c => c.Slots)
            .FirstOrDefaultAsync(ct);

        if (config is not null)
        {
            Slots = config.Slots
                .Where(s => s.IsActive)
                .OrderBy(s => s.Ordinal)
                .Select(s => new SlotRow(s.Id, s.Label))
                .ToList();
        }

        HasSlots = Slots.Count > 0;

        return Page();
    }

    public sealed record DayColumn(string DayName, string DateLabel, bool IsToday);
    public sealed record SlotRow(MealSlotId Id, string Label);
}
