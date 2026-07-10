using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Identity.Application;
using Plantry.Identity.Infrastructure;
using Plantry.Web.Tenancy;

namespace Plantry.Web.Pages.Account;

/// <summary>
/// /Account/Join?token=… — the anonymous accept page for a <see cref="Plantry.Identity.Domain.HouseholdInvite"/>
/// (plantry-mfli). GET validates the token (pending + unexpired) and either shows a registration form with the
/// invited email prefilled, or a friendly dead-end for an invalid/expired/used/revoked token. POST registers a
/// <b>new</b> user into the <b>inviting</b> household — contrast <see cref="RegisterModel"/>, which creates a
/// NEW household and seeds reference data. This flow deliberately runs neither the household-registration command
/// nor the reference-data seeders: the household already exists with its own units/categories/tags.
///
/// ADR-010 two-aggregate rule: user creation (Identity/UserManager) and invite acceptance are two steps, not one
/// transaction. We create the user FIRST, then mark the invite accepted — because a pending invite that already
/// produced its user is the recoverable state, whereas an accepted invite with no user is not. Everything here runs
/// with NO tenant context (the invitee is unauthenticated), so the household_invites / users RLS no-context
/// carve-out serves both the token lookup and the user insert.
/// </summary>
public sealed class JoinModel(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    HouseholdInviteService invites,
    ILogger<JoinModel> logger) : PageModel
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

        var householdId = validation.Value.HouseholdId.Value;

        // Step 1 (ADR-010): create the Identity user in the INVITING household. No RegisterHouseholdCommand,
        // no reference-data seeders — the household already has its data. Runs with no tenant context; the
        // identity.users RLS no-context carve-out permits the insert.
        var user = new AppUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = Input.DisplayName,
            HouseholdId = householdId
        };

        var identityResult = await userManager.CreateAsync(user, Input.Password);
        if (!identityResult.Succeeded)
        {
            foreach (var error in identityResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        // Stamp the household claim exactly as registration does — it is baked into the auth cookie and
        // drives RLS for every subsequent authenticated request.
        await userManager.AddClaimAsync(user, new Claim(HouseholdIdClaims.ClaimType, householdId.ToString()));

        // Step 2 (ADR-010): only now mark the invite accepted (one-way, records accepted_at). If this fails
        // (e.g. a concurrent double-submit accepted it first), the user already exists as a valid member —
        // the recoverable state. Log and continue rather than orphaning them or rolling back into the worse
        // "accepted invite, no user" state. (Concurrency hardening is tracked separately as plantry-bmfg.)
        var accept = await invites.AcceptAsync(Token, ct);
        if (accept.IsFailure)
        {
            logger.LogWarning(
                "Join: user {UserId} was created into household {HouseholdId} but AcceptAsync failed — {Code}: {Description}. "
                + "The user is a valid member; the invite was left unmarked.",
                user.Id, householdId, accept.Error.Code, accept.Error.Description);
        }

        // Happy-path domain event (Gate 9): a new member joined an existing household via an invite.
        // IDs only — never the email/display name (PII).
        logger.LogInformation(
            "Join: user {UserId} joined household {HouseholdId} via an accepted invite.", user.Id, householdId);

        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Today/Index");
    }

    private void SetDeadEnd(string message)
    {
        IsDeadEnd = true;
        DeadEndMessage = string.IsNullOrWhiteSpace(message)
            ? "This invite link is not valid."
            : message;
    }
}
