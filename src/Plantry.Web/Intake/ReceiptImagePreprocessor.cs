using ImageMagick;
using Plantry.SharedKernel;

namespace Plantry.Web.Intake;

/// <summary>
/// The (possibly downscaled) receipt image handed on to <c>ParseSessionCommand</c>. For an oversized
/// upload this is the re-encoded JPEG; for an in-bounds upload it is the original bytes verbatim.
/// </summary>
public sealed record ReceiptImage(byte[] Bytes, string ContentType);

/// <summary>
/// Downscales oversized receipt images server-side before the AI parse (plantry-v8vw). Vision models
/// tile images into tokens, so resolution beyond a legibility sweet spot only costs tokens, latency,
/// and (because the preprocessor's output is what <c>ImportReceipt</c> stores) database size — with no
/// OCR gain. Kept behind an interface so the upload handler is unit-testable without the native codec.
/// </summary>
public interface IReceiptImagePreprocessor
{
    /// <summary>
    /// Returns the image to hand to the parse command. Images whose longest edge is already ≤ 2048px pass
    /// through byte-identical with their original content type; larger images are auto-oriented, downscaled
    /// to a 2048px longest edge (aspect preserved, never upscaled) and re-encoded as JPEG q85. Bytes that
    /// cannot be decoded (corrupt or unsupported despite passing the magic-byte gate) return a structured
    /// failure — never an unhandled exception.
    /// </summary>
    Result<ReceiptImage> Process(byte[] imageBytes, string originalContentType);
}

/// <inheritdoc />
public sealed class ReceiptImagePreprocessor(ILogger<ReceiptImagePreprocessor> logger)
    : IReceiptImagePreprocessor
{
    /// <summary>Longest-edge ceiling in pixels — receipts stay OCR-legible well below iPhone's ~12MP.</summary>
    private const uint MaxLongestEdge = 2048;

    /// <summary>JPEG quality for the re-encode; 85 is visually lossless for text-heavy receipts.</summary>
    private const uint JpegQuality = 85;

    /// <summary>Structured failure surfaced through the upload handler's existing 400 (aij) path.</summary>
    public static readonly Error Undecodable = Error.Custom(
        "ReceiptImage.Undecodable",
        "We couldn't read that image. Try a clearer photo, or a JPEG, PNG, WebP, or HEIC file.");

    public Result<ReceiptImage> Process(byte[] imageBytes, string originalContentType)
    {
        try
        {
            // Cheap Ping: read dimensions from the header without a full decode. If the image is already
            // within bounds we return the original bytes untouched — no gratuitous transcode (HEIC included;
            // Gemini accepts image/heic), and never an upscale.
            var info = new MagickImageInfo(imageBytes);
            if (Math.Max(info.Width, info.Height) <= MaxLongestEdge)
                return new ReceiptImage(imageBytes, originalContentType);

            // Oversized: full decode → bake EXIF rotation into pixels (rotation can be lost on re-encode and
            // sideways pixels degrade OCR) → downscale the longest edge to 2048 preserving aspect → JPEG q85.
            using var image = new MagickImage(imageBytes);
            image.AutoOrient();
            image.Resize(new MagickGeometry(MaxLongestEdge, MaxLongestEdge) { IgnoreAspectRatio = false });
            image.Format = MagickFormat.Jpeg;
            image.Quality = JpegQuality;
            var jpegBytes = image.ToByteArray();

            logger.LogInformation(
                "Downscaled oversized receipt image ({OriginalWidth}x{OriginalHeight}, {OriginalContentType}) "
                    + "to {NewWidth}x{NewHeight} JPEG q{Quality}.",
                info.Width, info.Height, originalContentType, image.Width, image.Height, JpegQuality);

            return new ReceiptImage(jpegBytes, "image/jpeg");
        }
        catch (MagickException ex)
        {
            // The magic-byte gate upstream only inspects a structural prefix; a file can still be corrupt or
            // an unsupported variant. Surface a structured failure the handler renders as the aij 400 fragment.
            logger.LogWarning(ex,
                "Receipt image failed preprocessing (declared {OriginalContentType}); could not decode.",
                originalContentType);
            return Undecodable;
        }
    }
}
