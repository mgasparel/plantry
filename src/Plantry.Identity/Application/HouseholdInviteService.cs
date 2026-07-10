using Microsoft.Extensions.Logging;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Identity.Application;

/// <summary>
/// Result of a successful <see cref="HouseholdInviteService.AcceptAsync"/>: which household the
/// invitee joined and the invited email. Consumed by the join flow (plantry-mfli) to register the
/// new user into the inviting household and prefill their email.
/// </summary>
public readonly record struct AcceptedInvite(HouseholdId HouseholdId, string Email);

/// <summary>
/// Read model for a pending <see cref="HouseholdInvite"/>, surfaced on the Settings &gt; Members page:
/// the invitee email, the share <see cref="Token"/> (used to build the join link), who issued it, and
/// its lifecycle timestamps. <see cref="IsExpired"/> is evaluated against the current clock so the UI
/// can render lapsed-but-not-yet-swept invites inert (there is no background expiry sweep yet).
/// </summary>
public sealed record PendingInvite(
    HouseholdInviteId Id,
    string Email,
    string Token,
    Guid InvitedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired);

/// <summary>
/// The Identity application-layer operations over <see cref="HouseholdInvite"/> — issue, revoke, and
/// accept. This is the future roles-authorization seam: issue/revoke run under an authenticated
/// household (read from <see cref="ITenantContext"/>), while accept runs with <b>no tenant context</b>
/// (the invitee is unauthenticated) and resolves the invite by its unique token. Enforces invariants
/// R4 (only pending tokens accept; accept is one-way) and R5 (expiry checked on accept) via the
/// aggregate's guarded transitions.
/// </summary>
public sealed class HouseholdInviteService(
    IHouseholdInviteRepository invites,
    ITenantContext tenant,
    IClock clock,
    ILogger<HouseholdInviteService> logger)
{
    /// <summary>
    /// Issues a new invite for <paramref name="email"/> under the current household. Requires a household
    /// in context (the acting member's tenant); <paramref name="invitedByUserId"/> is the acting user for
    /// attribution. Returns the new invite's id, or <see cref="Error.Unauthorized"/> when unscoped.
    /// </summary>
    public async Task<Result<HouseholdInviteId>> IssueAsync(
        string email, Guid invitedByUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Error.Custom("Invite.EmailRequired", "An invitee email is required.");

        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("IssueInvite rejected — no household in context.");
            return Error.Unauthorized;
        }

        var invite = HouseholdInvite.Issue(HouseholdId.From(householdGuid), email, invitedByUserId, clock);
        await invites.AddAsync(invite, ct);
        await invites.SaveChangesAsync(ct);

        logger.LogInformation(
            "Invite {InviteId} issued for household {HouseholdId} by user {UserId}.",
            invite.Id.Value, householdGuid, invitedByUserId);

        return invite.Id;
    }

    /// <summary>
    /// Lists the current household's pending invites (most-recent first) as read models for the
    /// Settings &gt; Members roster. Returns an empty list when no household is in context. The
    /// tenant-scoped repository query guarantees a household only sees its own invites; each entry's
    /// <see cref="PendingInvite.IsExpired"/> is computed against the clock so the UI can mark lapsed
    /// invites inert.
    /// </summary>
    public async Task<IReadOnlyList<PendingInvite>> ListPendingAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var pending = await invites.ListPendingAsync(ct);
        return pending
            .Select(i => new PendingInvite(
                i.Id, i.Email, i.Token, i.InvitedByUserId, i.CreatedAt, i.ExpiresAt, i.IsExpired(clock)))
            .ToList();
    }

    /// <summary>
    /// Revokes a pending invite owned by the current household. Requires a household in context; the
    /// tenant-scoped lookup guarantees a household can only revoke its own invites. Returns
    /// <see cref="Error.NotFound"/> when no such invite exists for the active household, or a
    /// transition failure when the invite is not pending.
    /// </summary>
    public async Task<Result> RevokeAsync(HouseholdInviteId inviteId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
        {
            logger.LogWarning("RevokeInvite rejected — no household in context.");
            return Error.Unauthorized;
        }

        var invite = await invites.FindByIdAsync(inviteId, ct);
        if (invite is null)
        {
            logger.LogWarning("RevokeInvite rejected — invite {InviteId} not found for the active household.", inviteId.Value);
            return Error.NotFound;
        }

        var result = invite.Revoke();
        if (result.IsFailure)
        {
            logger.LogWarning(
                "RevokeInvite rejected for {InviteId} — {Code}: {Description}.",
                inviteId.Value, result.Error.Code, result.Error.Description);
            return result;
        }

        await invites.SaveChangesAsync(ct);
        logger.LogInformation("Invite {InviteId} revoked.", inviteId.Value);
        return Result.Success();
    }

    /// <summary>
    /// Accepts the invite identified by <paramref name="token"/>. MUST be called with <b>no tenant
    /// context</b> (the invitee is unauthenticated): the token lookup relies on the household_invites
    /// RLS no-context carve-out. Validates the invite is pending and unexpired (R4/R5), performs the
    /// one-way transition to accepted, and returns which household the invitee joined.
    /// </summary>
    public async Task<Result<AcceptedInvite>> AcceptAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Error.Custom("Invite.TokenRequired", "An invite token is required.");

        var invite = await invites.FindByTokenAsync(token, ct);
        if (invite is null)
        {
            logger.LogWarning("AcceptInvite rejected — token not found.");
            return Error.NotFound;
        }

        var result = invite.Accept(clock);
        if (result.IsFailure)
        {
            logger.LogWarning(
                "AcceptInvite rejected for {InviteId} — {Code}: {Description}.",
                invite.Id.Value, result.Error.Code, result.Error.Description);
            return result.Error;
        }

        await invites.SaveChangesAsync(ct);
        logger.LogInformation(
            "Invite {InviteId} accepted into household {HouseholdId}.", invite.Id.Value, invite.HouseholdId.Value);

        return new AcceptedInvite(invite.HouseholdId, invite.Email);
    }
}
