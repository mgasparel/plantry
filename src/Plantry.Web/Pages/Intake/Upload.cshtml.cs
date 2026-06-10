using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// SPEC §2a receipt upload (the hero AI intake entry point). The user picks a photo/file; the form
/// posts the raw bytes to <see cref="ParseSessionCommand"/>, which persists the image as the 1:1
/// <c>ImportReceipt</c> and runs the synchronous, untrusted AI parse. The command soft-fails to
/// <c>Failed</c> rather than throwing, so this page never trusts the parse — it only stages the
/// session and routes the user to the review form (Ready) or surfaces a retry affordance (Failed).
/// </summary>
[Authorize]
public sealed class UploadModel(
    IImportSessionRepository sessions,
    IReceiptParser parser,
    ICatalogHintProvider hints,
    IClock clock,
    ITenantContext tenant) : PageModel
{
    /// <summary>Accepted image content types — keeps obviously-wrong uploads off the AI pipeline.</summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/heic", "image/heif",
    };

    /// <summary>Upper bound on receipt image size (10 MB) — a cheap guard before we buffer bytes.</summary>
    private const long MaxImageBytes = 10 * 1024 * 1024;

    [BindProperty]
    [Required(ErrorMessage = "Choose a receipt photo to upload.")]
    public IFormFile? Receipt { get; set; }

    public void OnGet()
    {
    }

    /// <summary>Re-upload affordance from the failure fragment: swaps a fresh, empty form back in.</summary>
    public IActionResult OnGetRetry()
    {
        Receipt = null;
        return Partial("_UploadForm", this);
    }

    public async Task<IActionResult> OnPostParseAsync(CancellationToken ct)
    {
        if (Receipt is null || Receipt.Length == 0)
            ModelState.AddModelError(nameof(Receipt), "Choose a receipt photo to upload.");
        else if (Receipt.Length > MaxImageBytes)
            ModelState.AddModelError(nameof(Receipt), "That image is too large — keep it under 10 MB.");
        else if (!AllowedContentTypes.Contains(Receipt.ContentType))
            ModelState.AddModelError(nameof(Receipt), "Upload a photo (JPEG, PNG, WebP, or HEIC).");

        if (!ModelState.IsValid)
            return Partial("_UploadForm", this);

        byte[] imageBytes;
        await using (var stream = new MemoryStream())
        {
            await Receipt!.CopyToAsync(stream, ct);
            imageBytes = stream.ToArray();
        }

        var cmd = new ParseSessionCommand(
            imageBytes, Receipt.ContentType, CurrentUserId,
            sessions, parser, hints, clock, tenant);

        var result = await cmd.ExecuteAsync(ct);
        if (result.IsFailure)
        {
            // Pre-parse guard (e.g. unauthorized / empty image) — nothing was staged.
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return Partial("_UploadForm", this);
        }

        var sessionId = result.Value;
        var session = await sessions.FindAsync(sessionId, ct);

        // The parse runs inside ExecuteAsync; the session is now either Ready or Failed.
        if (session is null || session.Status == ImportStatus.Failed)
        {
            return Partial("_UploadFailed", new UploadFailedModel(
                sessionId,
                session?.ParseError ?? "We couldn't read that receipt. Try a clearer photo."));
        }

        // Ready — hand off to the review form. htmx follows HX-Redirect with a full navigation.
        var reviewUrl = Url.Page("/Intake/Review", new { id = sessionId.Value })!;
        Response.Headers["HX-Redirect"] = reviewUrl;
        return new EmptyResult();
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

/// <summary>View model for the parse-failure fragment: the staged session id (so the user has a record
/// of the attempt) plus the soft-fail message to surface, with a re-upload affordance.</summary>
public sealed record UploadFailedModel(ImportSessionId SessionId, string Message);
