using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// /Settings/Members — household membership administration. Lists current members (via
/// <see cref="IHouseholdDirectory"/>) and pending invites (via <see cref="HouseholdInviteService"/>),
/// and lets a member issue a new invite or revoke an outstanding one. Issuing an invite yields a
/// copy-able <c>/Account/Join?token=…</c> link (no email is sent in the alpha — the link is shared
/// manually). The invite/revoke handlers return the <c>_InvitesList</c> partial so the list stays live
/// without a full page reload.
/// </summary>
[Authorize]
public sealed class MembersModel(
    IHouseholdDirectory directory,
    HouseholdInviteService invites) : PageModel
{
    public IReadOnlyList<MemberViewModel> Members { get; private set; } = [];
    public IReadOnlyList<InviteViewModel> Invites { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public sealed record MemberViewModel(string DisplayName, string Initial);

    public sealed record InviteViewModel(
        Guid Id,
        string Email,
        string JoinUrl,
        string InvitedBy,
        DateTimeOffset ExpiresAt,
        bool IsExpired);

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // ── htmx fragment: return the current invites list ───────────────────────────

    public async Task<IActionResult> OnGetInvitesAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return Partial("Settings/_InvitesList", this);
    }

    // ── Issue an invite ──────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostInviteAsync(
        [FromForm] string? email,
        CancellationToken ct = default)
    {
        var result = await invites.IssueAsync(email ?? string.Empty, CurrentUserId, ct);
        if (result.IsFailure)
            ErrorMessage = result.Error.Description;
        await LoadAsync(ct);
        return Partial("Settings/_InvitesList", this);
    }

    // ── Revoke an invite ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRevokeAsync(
        [FromQuery] Guid id,
        CancellationToken ct = default)
    {
        var result = await invites.RevokeAsync(HouseholdInviteId.From(id), ct);
        if (result.IsFailure)
            ErrorMessage = result.Error.Description;
        await LoadAsync(ct);
        return Partial("Settings/_InvitesList", this);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        var members = await directory.ListMembersAsync(ct);
        Members = members
            .Select(m => new MemberViewModel(m.DisplayName, InitialOf(m.DisplayName)))
            .ToList();

        // Resolve "invited by" to a member display name where possible (best-effort — the inviter may
        // have left, in which case we fall back to a generic label).
        var namesByUserId = members
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);

        var pending = await invites.ListPendingAsync(ct);
        Invites = pending
            .Select(i => new InviteViewModel(
                i.Id.Value,
                i.Email,
                BuildJoinUrl(i.Token),
                namesByUserId.TryGetValue(i.InvitedByUserId.ToString(), out var name)
                    ? name
                    : "A household member",
                i.ExpiresAt,
                i.IsExpired))
            .ToList();
    }

    // Absolute join link the inviter shares manually. Built from the request rather than Url.Page
    // because the /Account/Join page is a sibling ticket (plantry-mfli) that may not exist yet.
    private string BuildJoinUrl(string token) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/Account/Join?token={Uri.EscapeDataString(token)}";

    private static string InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
