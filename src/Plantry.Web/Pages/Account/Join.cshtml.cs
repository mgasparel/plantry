using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;
using Plantry.Identity.Infrastructure;

namespace Plantry.Web.Pages.Account;

/// <summary>
/// /Account/Join?token=… — the anonymous accept page for a <see cref="Plantry.Identity.Domain.HouseholdInvite"/>
/// (plantry-mfli). GET validates the token (pending + unexpired) and either shows a registration form with the
/// invited email prefilled, or a friendly dead-end for an invalid/expired/used/revoked token. POST registers a
/// <b>new</b> user into the <b>inviting</b> household — contrast <see cref="RegisterModel"/>, which creates a
/// NEW household and seeds reference data. This flow deliberately runs neither the household-registration command
/// nor the reference-data seeders: the household already exists with its own units/categories/tags.
///
/// ADR-010 amendment (2026-07-11, plantry-bmfg): join-via-invite commits user-creation + invite-acceptance
/// <b>atomically</b> within the Identity context — both are ONE bounded context / ONE DbContext, so the
/// within-context transaction crosses no aggregate seam. This supersedes the earlier create-user-first,
/// log-and-continue ordering (plantry-mfli). The page no longer orchestrates the saga: it runs the pre-checks
/// (validate token, ModelState, email-not-already-a-user) OUTSIDE the transaction, then hands the three writes
/// to <see cref="JoinHouseholdCommand"/>, which wraps create-user → stamp-claim → accept-invite in one
/// transaction. If acceptance loses the single-use race, the command rolls back (the user INSERT vanishes) and
/// this page shows the dead-end. <see cref="SignInManager{TUser}.SignInAsync(TUser, bool, string)"/> runs strictly
/// AFTER the commit. Everything runs with NO tenant context (the invitee is unauthenticated), so the
/// household_invites / users RLS no-context carve-out serves both the token lookup and the user insert.
/// </summary>
public sealed class JoinModel(
    SignInManager<AppUser> signInManager,
    HouseholdInviteService invites,
    JoinHouseholdCommand joinHousehold,
    UserManager<AppUser> userManager) : PageModel
{
    /// <summary>The invite token, carried from the query string on GET and round-tripped through the POST form.</summary>
    [BindProperty]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True when the token is invalid/expired/used and the page should show the dead-end instead of the form.</summary>
    public bool IsDeadEnd { get; private set; }

    /// <summary>The friendly explanation shown on the dead-end (never an exception / stack trace).</summary>
    public string DeadEndMessage { get; private set; } = string.Empty;

    public sealed class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        [Display(Name = "Your name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required, MinLength(8)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken ct = default)
    {
        // An already-authenticated user following a join link would be an existing-user household
        // migration — explicitly out of scope for the alpha (epic plantry-1dao). Send them home.
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Today/Index");

        Token = token ?? string.Empty;

        var result = await invites.ValidateTokenAsync(Token, ct);
        if (result.IsFailure)
        {
            SetDeadEnd(result.Error.Description);
            return Page();
        }

        // Prefill the invited email (editable — the token is the credential, email match is not enforced).
        Input.Email = result.Value.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        // Symmetry with OnGetAsync: an already-authenticated user has no business creating a second
        // account via a join link (existing-user migration is out of scope). Send them home rather than
        // silently minting a new user and switching their session onto it.
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Today/Index");

        // Re-validate the token on POST: it may have been revoked or expired between GET and submit, and
        // the household to register into comes from the (unauthenticated) token, never from form input.
        var validation = await invites.ValidateTokenAsync(Token, ct);
        if (validation.IsFailure)
        {
            SetDeadEnd(validation.Error.Description);
            return Page();
        }

        if (!ModelState.IsValid)
            return Page();

        // New users only: existing-user migration is out of scope (epic plantry-1dao). Surface a clear
        // message rather than letting Identity's unique-email index throw a generic duplicate error.
        var existing = await userManager.FindByEmailAsync(Input.Email);
        if (existing is not null)
        {
            ModelState.AddModelError(
                "Input.Email",
                "An account with this email already exists. Sign in instead, or ask the person who invited you to send the link to a new email.");
            return Page();
        }

        // Hand the three writes (create user → stamp claim → accept invite) to the Identity-module command,
        // which runs them as ONE transaction (ADR-010 amendment, plantry-bmfg). We DON'T sign in here — the
        // command owns the atomic write; sign-in happens only after it reports a committed success.
        var join = await joinHousehold.ExecuteAsync(
            Token,
            validation.Value.HouseholdId,
            Input.Email,
            Input.DisplayName,
            Input.Password,
            ct);

        switch (join.Outcome)
        {
            case JoinOutcome.UserCreationFailed:
                foreach (var error in join.UserCreationErrors)
                    ModelState.AddModelError(string.Empty, error.Description);
                return Page();

            case JoinOutcome.InviteUnavailable:
                // The invite was accepted/revoked/expired between our pre-check and the write — most often a
                // concurrent double-submit that another request won. The command already rolled back, so no
                // second user exists. Show the friendly dead-end rather than a stale form.
                SetDeadEnd(join.InviteError.Description);
                return Page();

            case JoinOutcome.Succeeded:
                // Sign in strictly AFTER the commit (the command returns only on a committed success).
                await signInManager.SignInAsync(join.User!, isPersistent: false);
                return RedirectToPage("/Today/Index");

            default:
                throw new InvalidOperationException($"Unhandled join outcome '{join.Outcome}'.");
        }
    }

    private void SetDeadEnd(string message)
    {
        IsDeadEnd = true;
        DeadEndMessage = string.IsNullOrWhiteSpace(message)
            ? "This invite link is not valid."
            : message;
    }
}
