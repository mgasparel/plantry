using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Plantry.Identity.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Identity.Infrastructure;

/// <summary>How a <see cref="JoinHouseholdCommand.ExecuteAsync"/> attempt resolved.</summary>
public enum JoinOutcome
{
    /// <summary>User created, household claim stamped, invite accepted, transaction committed.</summary>
    Succeeded,

    /// <summary>ASP.NET Identity rejected the new user (e.g. weak password). Nothing was committed.</summary>
    UserCreationFailed,

    /// <summary>The invite could not be accepted (expired/revoked/used, or it lost a concurrent race).
    /// The whole transaction rolled back — the user INSERT vanished, so no partial member exists.</summary>
    InviteUnavailable,
}

/// <summary>
/// Outcome of a join attempt. Exactly one of the three shapes is populated per <see cref="Outcome"/>:
/// <see cref="User"/> on success, <see cref="UserCreationErrors"/> on <see cref="JoinOutcome.UserCreationFailed"/>,
/// <see cref="InviteError"/> on <see cref="JoinOutcome.InviteUnavailable"/>.
/// </summary>
public sealed class JoinHouseholdResult
{
    public required JoinOutcome Outcome { get; init; }

    /// <summary>The signed-up member — set only when <see cref="Outcome"/> is <see cref="JoinOutcome.Succeeded"/>.
    /// The caller signs this user in <b>after</b> the transaction has committed.</summary>
    public AppUser? User { get; init; }

    public IReadOnlyList<IdentityError> UserCreationErrors { get; init; } = [];

    public Error InviteError { get; init; } = Error.None;

    public static JoinHouseholdResult Succeeded(AppUser user) =>
        new() { Outcome = JoinOutcome.Succeeded, User = user };

    public static JoinHouseholdResult UserCreationFailed(IEnumerable<IdentityError> errors) =>
        new() { Outcome = JoinOutcome.UserCreationFailed, UserCreationErrors = errors.ToList() };

    public static JoinHouseholdResult InviteUnavailable(Error error) =>
        new() { Outcome = JoinOutcome.InviteUnavailable, InviteError = error };
}

/// <summary>
/// The Identity-module command that joins a new user into an existing household via an invite, as ONE
/// ACID transaction on the shared <see cref="PlantryIdentityDbContext"/> (plantry-bmfg). Extracted out
/// of the Join page so the page no longer orchestrates a cross-aggregate saga — it just runs its
/// pre-checks, calls this, and maps the outcome.
/// <para>
/// The three writes — create the Identity user, stamp its household claim, accept the invite — all run
/// on the same scoped connection (UserManager and <see cref="HouseholdInviteService"/> share this
/// context) inside one <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>. Because
/// user-creation and acceptance live in ONE bounded context / ONE DbContext, a within-context transaction
/// crosses no aggregate seam — this is the ADR-010 amendment (2026-07-11) that supersedes the
/// create-user-first, log-and-continue ordering. If acceptance fails (invalid, or it lost the concurrent
/// single-use race), we do NOT commit: the transaction disposes and rolls back, the user INSERT and claim
/// vanish, and there is zero partial state. Rollback IS the compensation — no domain Reopen, no reservation
/// state machine. Precedent for explicit-tx orchestration: Deals (plantry-jvzk).
/// </para>
/// Runs with NO tenant context (the invitee is unauthenticated) so the identity RLS no-context carve-out
/// serves both the token lookup and the user insert.
/// </summary>
public sealed class JoinHouseholdCommand(
    UserManager<AppUser> userManager,
    PlantryIdentityDbContext db,
    HouseholdInviteService invites,
    ILogger<JoinHouseholdCommand> logger)
{
    /// <summary>
    /// Executes the join. <paramref name="householdId"/> and the invitee inputs are assumed already
    /// validated by the caller's pre-checks (token still valid, ModelState valid, email not already a
    /// user) — those checks stay OUTSIDE the transaction; only the three writes are wrapped.
    /// </summary>
    public async Task<JoinHouseholdResult> ExecuteAsync(
        string token,
        HouseholdId householdId,
        string email,
        string displayName,
        string password,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Step 1: create the Identity user in the INVITING household. No RegisterHouseholdCommand and no
        // reference-data seeders — the household already has its units/categories/tags.
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            HouseholdId = householdId.Value,
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            // Nothing durable to undo yet, but let the transaction roll back on dispose for symmetry.
            return JoinHouseholdResult.UserCreationFailed(created.Errors);
        }

        // Step 2: stamp the household claim exactly as registration does — baked into the auth cookie,
        // drives RLS on every subsequent authenticated request.
        await userManager.AddClaimAsync(
            user, new Claim(HouseholdClaimTypes.HouseholdId, householdId.Value.ToString()));

        // Step 3: accept the invite, stamping this new user as accepted_by. On the losing side of a
        // concurrent double-submit this returns a not-pending failure (the xmin guard fired) rather than
        // throwing — we roll back below, so the loser's user + claim never persist.
        var accept = await invites.AcceptAsync(token, Guid.Parse(user.Id), ct);
        if (accept.IsFailure)
        {
            logger.LogWarning(
                "Join rolled back for household {HouseholdId} — invite unacceptable ({Code}). No member created.",
                householdId.Value, accept.Error.Code);
            return JoinHouseholdResult.InviteUnavailable(accept.Error);
        }

        await tx.CommitAsync(ct);

        // IDs only (Gate 9) — never the email/display name (PII).
        logger.LogInformation(
            "Join committed: user {UserId} joined household {HouseholdId} via an accepted invite.",
            user.Id, householdId.Value);

        return JoinHouseholdResult.Succeeded(user);
    }
}
