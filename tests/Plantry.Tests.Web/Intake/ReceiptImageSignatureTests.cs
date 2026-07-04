using System.Text;
using Plantry.Web.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// Unit coverage for the magic-byte sniffer (<see cref="ReceiptImageSignature"/>): the accepted formats
/// (jpeg/png/webp/heic/heif) are recognised structurally, and everything else — including a file whose
/// bytes contradict a spoofed image content-type — is rejected. Keeps the abuse gate honest without
/// booting the web host.
/// </summary>
public sealed class ReceiptImageSignatureTests
{
    // ── Accepted formats ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Jpeg_soi_marker_is_accepted()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        Assert.True(ReceiptImageSignature.IsAllowedImage(jpeg));
        Assert.Equal("jpeg", ReceiptImageSignature.DetectFormat(jpeg));
    }

    [Fact]
    public void Png_signature_is_accepted()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
        Assert.True(ReceiptImageSignature.IsAllowedImage(png));
        Assert.Equal("png", ReceiptImageSignature.DetectFormat(png));
    }

    [Fact]
    public void Webp_riff_container_is_accepted()
    {
        var webp = new byte[16];
        "RIFF"u8.CopyTo(webp);          // bytes 0..4
        // bytes 4..8 = file size (any value)
        "WEBP"u8.CopyTo(webp.AsSpan(8)); // bytes 8..12
        Assert.True(ReceiptImageSignature.IsAllowedImage(webp));
        Assert.Equal("webp", ReceiptImageSignature.DetectFormat(webp));
    }

    [Theory]
    [InlineData("heic")] // iPhone still photo (HEIC)
    [InlineData("heix")]
    [InlineData("mif1")] // iPhone image container
    [InlineData("heif")]
    [InlineData("msf1")]
    public void Heif_family_brands_are_accepted(string brand)
    {
        // ISO-BMFF ftyp box: [size:4]["ftyp"][major-brand:4]...
        var heif = new byte[16];
        heif[3] = 0x10; // box size = 16
        "ftyp"u8.CopyTo(heif.AsSpan(4));
        Encoding.ASCII.GetBytes(brand).CopyTo(heif.AsSpan(8));
        Assert.True(ReceiptImageSignature.IsAllowedImage(heif));
        Assert.Equal("heif", ReceiptImageSignature.DetectFormat(heif));
    }

    // ── Rejected inputs ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pdf_is_rejected_even_when_declared_an_image()
    {
        // The spoof case: a real PDF whose upload claims Content-Type: image/png.
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%binary-marker\n");
        Assert.False(ReceiptImageSignature.IsAllowedImage(pdf));
        Assert.Null(ReceiptImageSignature.DetectFormat(pdf));
    }

    [Fact]
    public void Gif_is_rejected()
    {
        byte[] gif = Encoding.ASCII.GetBytes("GIF89a");
        Assert.False(ReceiptImageSignature.IsAllowedImage(gif));
    }

    [Fact]
    public void Ftyp_box_with_a_non_heif_brand_is_rejected()
    {
        // An MP4 video ("ftyp" + "isom") shares the ISO-BMFF container but is not an accepted image.
        var mp4 = new byte[16];
        mp4[3] = 0x10;
        "ftyp"u8.CopyTo(mp4.AsSpan(4));
        "isom"u8.CopyTo(mp4.AsSpan(8));
        Assert.False(ReceiptImageSignature.IsAllowedImage(mp4));
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.False(ReceiptImageSignature.IsAllowedImage(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Truncated_signature_is_rejected()
    {
        // Only the first two JPEG bytes — not enough to confirm the marker.
        byte[] truncated = [0xFF, 0xD8];
        Assert.False(ReceiptImageSignature.IsAllowedImage(truncated));
    }

    [Fact]
    public void Random_bytes_are_rejected()
    {
        byte[] noise = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C];
        Assert.False(ReceiptImageSignature.IsAllowedImage(noise));
    }
}
