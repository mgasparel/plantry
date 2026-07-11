using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Settings;

[Authorize]
public sealed class MealSlotsModel(ManageSlotsService service, ITenantContext tenant) : PageModel
{
    public List<SlotViewModel> Slots { get; private set; } = [];
    public List<HouseholdMember> Members { get; private set; } = [];

    public sealed record SlotViewModel(
        MealSlotId Id,
        string Label,
        int Ordinal,
        List<Guid> DefaultAttendees,
        bool IncludeInAutoPlan);

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
    }

    // htmx fragment — returns the full slots list partial
    public async Task<IActionResult> OnGetSlotsAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostAddAsync(
        [FromForm] string label,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return BadRequest();
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        try { await service.AddSlotAsync(hid, label.Trim(), ct); }
        catch (InvalidOperationException ex) { ModelState.AddModelError("label", ex.Message); }
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostRenameAsync(
        [FromQuery] Guid id,
        [FromForm] string label,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return BadRequest();
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        try { await service.RenameSlotAsync(hid, MealSlotId.From(id), label.Trim(), ct); }
        catch (InvalidOperationException) { /* label unchanged — tolerate */ }
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostReorderAsync(
        [FromForm] List<Guid> ids,
        CancellationToken ct = default)
    {
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        var slotIds = ids.Select(MealSlotId.From).ToList();
        try { await service.ReorderSlotsAsync(hid, slotIds, ct); }
        catch (InvalidOperationException) { /* ignore reorder of unknown ids */ }
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostAttendeesAsync(
        [FromQuery] Guid id,
        [FromForm] List<Guid> memberIds,
        CancellationToken ct = default)
    {
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        await service.SetDefaultAttendeesAsync(hid, MealSlotId.From(id), memberIds, ct);
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostAutoPlanAsync(
        [FromQuery] Guid id,
        [FromForm] bool enabled,
        CancellationToken ct = default)
    {
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        try { await service.SetAutoPlanEnabledAsync(hid, MealSlotId.From(id), enabled, ct); }
        catch (InvalidOperationException) { /* slot not found — already archived */ }
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    public async Task<IActionResult> OnPostArchiveAsync(
        [FromQuery] Guid id,
        CancellationToken ct = default)
    {
        var hid = HouseholdId.From(tenant.HouseholdId!.Value);
        try { await service.ArchiveSlotAsync(hid, MealSlotId.From(id), ct); }
        catch (InvalidOperationException) { /* slot not found — already archived */ }
        await LoadAsync(ct);
        return Partial("_SlotsList", this);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var householdGuid = tenant.HouseholdId ?? Guid.Empty;
        var hid = HouseholdId.From(householdGuid);
        var config = await service.GetSlotsAsync(hid, ct);
        Members = (await service.ListMembersAsync(ct)).ToList();
        Slots = config?.Slots
            .Where(s => s.IsActive)
            .OrderBy(s => s.Ordinal)
            .Select(s => new SlotViewModel(s.Id, s.Label, s.Ordinal, [..s.DefaultAttendees], s.IncludeInAutoPlan))
            .ToList() ?? [];
    }
}
