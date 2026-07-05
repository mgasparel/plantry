using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Web.Intake;
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
// Pre-buffer abuse guards: reject an oversized upload at the request level, BEFORE ASP.NET buffers the
// body, in addition to the post-bind MaxImageBytes check below (defense-in-depth + a friendly message for
// sub-limit files). RequestSizeLimit caps the whole request body; RequestFormLimits caps the multipart part.
[RequestSizeLimit(MaxImageBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxImageBytes)]
public sealed class UploadModel(
    IImportSessionRepository sessions,
    IReceiptParser parser,
    ICatalogHintProvider hints,
    IClock clock,
    ITenantContext tenant,
    ReceiptUploadRateLimiter uploadRateLimiter,
    IReceiptImagePreprocessor imagePreprocessor,
    ILogger<UploadModel> logger,
    ILogger<ParseSessionCommand> parseLogger) : PageModel
{
    public IReadOnlyList<RecentIntakeRow> RecentIntakes { get; private set; } = [];
    public bool AiAvailable => parser is not DisabledReceiptParser;
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

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (tenant.HouseholdId is { } hid)
            RecentIntakes = await new GetRecentSessionsQuery(sessions)
                .ExecuteAsync(HouseholdId.From(hid), take: 8, ct);
    }

    /// <summary>Re-upload affordance from the failure fragment: swaps a fresh, empty form back in.</summary>
    public IActionResult OnGetRetry()
    {
        Receipt = null;
        return Partial("_UploadForm", this);
    }

    /// <summary>
    /// Submits a built-in sample receipt through the same parse pipeline as a real upload.
    /// Available in Development when AI:UseSampleParser=true; returns a model error otherwise so
    /// the button degrades gracefully rather than throwing.
    /// </summary>
    public async Task<IActionResult> OnPostSampleAsync(CancellationToken ct)
    {
        // Placeholder bytes — the SampleReceiptParser ignores image content entirely.
        byte[] sampleBytes = [0xFF, 0xD8, 0xFF, 0xE0]; // minimal JPEG header

        var cmd = new ParseSessionCommand(
            sampleBytes, "image/jpeg", CurrentUserId,
            sessions, parser, hints, clock, tenant, parseLogger);

        var result = await cmd.ExecuteAsync(ct);
        if (result.IsFailure)
        {
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return Partial("_UploadForm", this);
        }

        var sessionId = result.Value;
        var session = await sessions.FindAsync(sessionId, ct);

        if (session is null || session.Status == ImportStatus.Failed)
        {
            return Partial("_UploadFailed", new UploadFailedModel(
                sessionId,
                session?.ParseError ?? "The sample receipt couldn't be parsed. Is UseSampleParser enabled?"));
        }

        var reviewUrl = Url.Page("/Intake/Review", new { id = sessionId.Value })!;
        Response.Headers["HX-Redirect"] = reviewUrl;
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostParseAsync(CancellationToken ct)
    {
        // ── Abuse gate 1: per-household rate limit (burst + daily). Checked before we read the body so a
        // flood is rejected cheaply. Rejection returns a structured 429 + Retry-After, surfaced coherently
        // as the upload form fragment (htmx swaps it via the form's before-swap handler). ──
        var partitionKey = tenant.HouseholdId?.ToString() ?? CurrentUserId.ToString();
        using (var lease = uploadRateLimiter.AttemptAcquire(partitionKey))
        {
            if (!lease.IsAcquired)
            {
                var retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                    : 60;
                Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                logger.LogWarning(
                    "Receipt upload rate-limited for {PartitionKey}; retry after {RetryAfterSeconds}s.",
                    partitionKey, retryAfterSeconds);
                ModelState.AddModelError(string.Empty,
                    "You've uploaded a lot of receipts in a short time. Please wait a moment and try again.");
                return UploadFormResult(StatusCodes.Status429TooManyRequests);
            }
        }

        if (Receipt is null || Receipt.Length == 0)
            ModelState.AddModelError(nameof(Receipt), "Choose a receipt photo to upload.");
        else if (Receipt.Length > MaxImageBytes)
            ModelState.AddModelError(nameof(Receipt), "That image is too large — keep it under 10 MB.");
        else if (!AllowedContentTypes.Contains(Receipt.ContentType))
            ModelState.AddModelError(nameof(Receipt), "Upload a photo (JPEG, PNG, WebP, or HEIC).");

        if (!ModelState.IsValid)
            return UploadFormResult(StatusCodes.Status400BadRequest);

        byte[] imageBytes;
        await using (var stream = new MemoryStream())
        {
            await Receipt!.CopyToAsync(stream, ct);
            imageBytes = stream.ToArray();
        }

        // ── Abuse gate 2: magic-byte sniff. The Content-Type header is spoofable, so confirm the leading
        // bytes are actually one of the allowed formats (jpeg/png/webp/heic/heif) before staging anything
        // for the AI pipeline. A header/body mismatch is a structured 400. ──
        if (!ReceiptImageSignature.IsAllowedImage(imageBytes))
        {
            logger.LogWarning(
                "Rejected receipt upload for {PartitionKey}: declared {ContentType} but the bytes are not a supported image.",
                partitionKey, Receipt.ContentType);
            ModelState.AddModelError(nameof(Receipt),
                "That file doesn't look like a supported image. Upload a JPEG, PNG, WebP, or HEIC photo.");
            return UploadFormResult(StatusCodes.Status400BadRequest);
        }

        // ── Preprocess (plantry-v8vw): downscale an oversized photo (longest edge > 2048px) to a JPEG q85
        // before staging, so the stored ImportReceipt and the AI parse both work off the smaller image. An
        // in-bounds image passes through byte-identical. A decode failure here (corrupt/unsupported despite
        // the magic-byte gate) is surfaced as the same structured 400 fragment, never a downstream crash. ──
        var preprocessed = imagePreprocessor.Process(imageBytes, Receipt.ContentType);
        if (preprocessed.IsFailure)
        {
            logger.LogWarning(
                "Rejected receipt upload for {PartitionKey}: image failed preprocessing ({ErrorCode}).",
                partitionKey, preprocessed.Error.Code);
            ModelState.AddModelError(nameof(Receipt), preprocessed.Error.Description);
            return UploadFormResult(StatusCodes.Status400BadRequest);
        }

        var cmd = new ParseSessionCommand(
            preprocessed.Value.Bytes, preprocessed.Value.ContentType, CurrentUserId,
            sessions, parser, hints, clock, tenant, parseLogger);

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

    /// <summary>
    /// Re-renders the upload form fragment with the given HTTP status. Validation and abuse rejections
    /// carry a structured 400/429 (rather than a bland 200) while still returning the fragment htmx swaps
    /// into <c>#upload-region</c>, keeping the page-model validation UX coherent with the status code.
    /// </summary>
    private PartialViewResult UploadFormResult(int statusCode)
    {
        var result = Partial("_UploadForm", this);
        result.StatusCode = statusCode;
        return result;
    }
}

/// <summary>View model for the parse-failure fragment: the staged session id (so the user has a record
/// of the attempt) plus the soft-fail message to surface, with a re-upload affordance.</summary>
public sealed record UploadFailedModel(ImportSessionId SessionId, string Message);
