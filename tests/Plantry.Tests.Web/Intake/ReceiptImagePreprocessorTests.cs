using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Web.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L1/L2 unit tests for <see cref="ReceiptImagePreprocessor"/> (plantry-v8vw): the pure downscale/transcode
/// logic, exercised directly (no web host) so the fast tier pins the behaviour. Covers the acceptance
/// criteria: oversized JPEG downscale, the HEIC→JPEG re-encode (the criterion SkiaSharp could not meet —
/// must be green on the Windows pre-flight runtime and the Debian deploy image), in-bounds byte-identical
/// pass-through, EXIF auto-orientation, and the structured failure for undecodable bytes.
///
/// <para>JPEG/PNG inputs are generated in-test (Magick.NET encodes them). The HEIC and EXIF-rotated
/// fixtures are committed binaries under <c>Intake/Fixtures/</c>: Magick.NET-Q8 decodes HEIC but cannot
/// <em>encode</em> it, and its JPEG writer drops a synthesised EXIF orientation tag, so neither can be
/// produced at test time — they must be real assets.</para>
/// </summary>
public sealed class ReceiptImagePreprocessorTests
{
    private const uint MaxLongestEdge = 2048;

    private static readonly IReceiptImagePreprocessor Sut =
        new ReceiptImagePreprocessor(NullLogger<ReceiptImagePreprocessor>.Instance);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Intake", "Fixtures", name);

    // ── Test 1: oversized JPEG → downscaled JPEG at a 2048px longest edge, aspect preserved ──────────
    [Fact]
    public void Oversized_jpeg_is_downscaled_to_2048_longest_edge_preserving_aspect()
    {
        var input = MakeImage(3000, 2000, MagickFormat.Jpeg);

        var result = Sut.Process(input, "image/jpeg");

        Assert.True(result.IsSuccess);
        Assert.Equal("image/jpeg", result.Value.ContentType);

        using var output = new MagickImage(result.Value.Bytes);
        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.Equal(MaxLongestEdge, Math.Max(output.Width, output.Height));
        // 3000x2000 (1.5) → 2048x1365 (~1.4998): aspect preserved within rounding.
        var aspect = (double)output.Width / output.Height;
        Assert.InRange(aspect, 1.49, 1.51);
    }

    // ── Test 2: oversized HEIC → decoded and re-encoded as JPEG ≤ 2048 (the SkiaSharp criterion) ─────
    [Fact]
    public void Oversized_heic_is_decoded_and_reencoded_to_jpeg()
    {
        var input = File.ReadAllBytes(FixturePath("receipt-oversized.heic"));
        // Guard: the fixture really is an oversized HEIC, so this test exercises the transcode path.
        var inInfo = new MagickImageInfo(input);
        Assert.Equal(MagickFormat.Heic, inInfo.Format);
        Assert.True(Math.Max(inInfo.Width, inInfo.Height) > MaxLongestEdge);

        var result = Sut.Process(input, "image/heic");

        Assert.True(result.IsSuccess);
        Assert.Equal("image/jpeg", result.Value.ContentType);

        using var output = new MagickImage(result.Value.Bytes);
        Assert.Equal(MagickFormat.Jpeg, output.Format);
        Assert.Equal(MaxLongestEdge, Math.Max(output.Width, output.Height));
    }

    // ── Test 3: in-bounds image → returned byte-identical, content type unchanged (no upscale/transcode) ─
    [Fact]
    public void In_bounds_image_passes_through_byte_identical()
    {
        var input = MakeImage(800, 600, MagickFormat.Png);

        var result = Sut.Process(input, "image/png");

        Assert.True(result.IsSuccess);
        Assert.Equal("image/png", result.Value.ContentType);
        // Same array reference: proves the bytes were neither re-encoded nor upscaled.
        Assert.Same(input, result.Value.Bytes);
    }

    [Fact]
    public void Image_exactly_at_the_ceiling_passes_through_byte_identical()
    {
        // Boundary: longest edge == 2048 is in-bounds (the guard is strictly greater-than).
        var input = MakeImage(MaxLongestEdge, 1000, MagickFormat.Png);

        var result = Sut.Process(input, "image/png");

        Assert.True(result.IsSuccess);
        Assert.Same(input, result.Value.Bytes);
    }

    // ── Test 4: EXIF-rotated oversized JPEG → output pixels upright (rotation baked in) ──────────────
    [Fact]
    public void Exif_rotated_oversized_jpeg_is_uprighted_on_the_transcode_path()
    {
        var input = File.ReadAllBytes(FixturePath("receipt-rotated.jpg"));
        // The fixture is stored landscape (3000x2000) with EXIF Orientation=RightTop, i.e. it should
        // display rotated 90° to portrait. Guard those preconditions so the assertion below is meaningful.
        using (var stored = new MagickImage(input))
        {
            Assert.True(stored.Width > stored.Height, "Fixture must be stored landscape.");
            Assert.Equal(OrientationType.RightTop, stored.Orientation);
        }

        var result = Sut.Process(input, "image/jpeg");

        Assert.True(result.IsSuccess);
        using var output = new MagickImage(result.Value.Bytes);
        // AutoOrient baked the rotation into pixels: the output is now portrait and its orientation tag
        // is neutral (TopLeft/Undefined), so no downstream viewer will rotate it a second time.
        Assert.True(output.Height > output.Width, "Auto-oriented output must be portrait.");
        Assert.True(
            output.Orientation is OrientationType.TopLeft or OrientationType.Undefined,
            $"Orientation must be neutralised, was {output.Orientation}.");
        Assert.Equal(MaxLongestEdge, Math.Max(output.Width, output.Height));
    }

    // ── Test 5: corrupt bytes with a valid magic prefix → structured failure (no exception) ──────────
    [Fact]
    public void Corrupt_bytes_with_a_valid_jpeg_prefix_return_a_structured_failure()
    {
        // Valid JPEG SOI marker (passes the upstream magic-byte gate) followed by non-image garbage.
        byte[] corrupt = [0xFF, 0xD8, 0xFF, .. new byte[512]];
        for (var i = 3; i < corrupt.Length; i++)
            corrupt[i] = (byte)(i * 7);

        var result = Sut.Process(corrupt, "image/jpeg");

        Assert.True(result.IsFailure);
        Assert.Equal(ReceiptImagePreprocessor.Undecodable.Code, result.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Description));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Encodes a solid test image of the given size and format (Magick.NET encodes JPEG/PNG).</summary>
    private static byte[] MakeImage(uint width, uint height, MagickFormat format)
    {
        using var image = new MagickImage(new MagickColor("white"), width, height);
        image.Format = format;
        if (format == MagickFormat.Jpeg)
            image.Quality = 92;
        return image.ToByteArray();
    }
}
