using System.Text;

namespace Plantry.Web.Intake;

/// <summary>
/// Content-sniffing guard for receipt uploads: confirms the uploaded bytes are actually one of the
/// allowed raster image formats by inspecting the leading "magic bytes", rather than trusting the
/// client-supplied <c>Content-Type</c> header (which is trivially spoofable).
///
/// <para>The accepted set is deliberately identical to the upload handler's <c>AllowedContentTypes</c>
/// allowlist — JPEG, PNG, WebP, HEIC/HEIF — with HEIC/HEIF kept because they are the default iPhone
/// camera formats. PDF and everything else are rejected (the AI parser is image-based). This is a
/// cheap structural check, not a full decode: a file that passes here is plausibly an image of the
/// declared family, which is all the abuse gate needs before staging bytes for the pipeline.</para>
/// </summary>
public static class ReceiptImageSignature
{
    /// <summary>True when the leading bytes match JPEG, PNG, WebP, or HEIC/HEIF.</summary>
    public static bool IsAllowedImage(ReadOnlySpan<byte> bytes) => DetectFormat(bytes) is not null;

    /// <summary>Returns a short format tag ("jpeg"/"png"/"webp"/"heif") or <c>null</c> if unrecognised.</summary>
    public static string? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (IsJpeg(bytes)) return "jpeg";
        if (IsPng(bytes)) return "png";
        if (IsWebp(bytes)) return "webp";
        if (IsHeif(bytes)) return "heif";
        return null;
    }

    // JPEG (incl. JFIF/EXIF): SOI marker FF D8 followed by the first marker's FF.
    private static bool IsJpeg(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    // PNG: the fixed 8-byte signature.
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(PngMagic);

    // WebP: RIFF container — "RIFF" at [0..4), 4-byte length, then "WEBP" at [8..12).
    private static bool IsWebp(ReadOnlySpan<byte> b) =>
        b.Length >= 12
        && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
        && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    // HEIC/HEIF: ISO base media file format — [box-size:4]["ftyp"]{major-brand:4} where the major
    // brand is one of the HEIF family. Covers what iPhones emit (heic/heix/mif1) plus the wider set.
    private static readonly HashSet<string> HeifBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "heif", "mif1", "msf1",
    };

    private static bool IsHeif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return false;
        // "ftyp" box type at offset 4.
        if (!(b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p'))
            return false;
        var majorBrand = Encoding.ASCII.GetString(b.Slice(8, 4));
        return HeifBrands.Contains(majorBrand);
    }
}
