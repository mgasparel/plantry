using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 boundary assertions for the receipt-upload abuse gate (plantry-aij), exercised through the real
/// <c>Plantry.Web</c> pipeline via <see cref="UploadFragmentFactory"/>:
///   1. A valid image clears every gate (not rejected).
///   2. A spoofed Content-Type whose bytes are not an image is rejected with a structured 400.
///   3. HEIC bytes (the iPhone default) are accepted at the gate.
///   4. Bursting past the per-household limit returns a structured 429 with a Retry-After header.
///   5. An oversized body is rejected at the request level (before the handler stages it).
/// </summary>
public sealed class UploadAbuseGuardTests(UploadFragmentFactory factory) : IClassFixture<UploadFragmentFactory>
{
    private const string UploadUrl = "/Intake/Upload";
    private const string ParseUrl = "/Intake/Upload?handler=Parse";

    // A real, decodable in-bounds PNG. Since plantry-v8vw the upload handler decodes the image (to downscale
    // oversized photos) before staging it, so an "admitted" upload must be a genuine image, not a bare magic
    // prefix. Kept small (in-bounds) so it passes through the preprocessor byte-identical.
    private static byte[] ValidPng()
    {
        using var image = new MagickImage(new MagickColor("white"), 64, 64);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    [Fact]
    public async Task Valid_png_clears_the_abuse_gates()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, ValidPng(), "image/png", "receipt.png"));

        // Not rejected by any abuse gate (400/413/429). With the no-key parser it lands on the soft-fail
        // fragment (200), which is fine — the point is that it was admitted past the gates.
        Assert.True((int)response.StatusCode < 400,
            $"A valid PNG must clear the abuse gates, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Spoofed_content_type_with_non_image_bytes_is_rejected_with_400()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        // A PDF masquerading as a PNG via a forged Content-Type header.
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%binary\n");
        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, pdfBytes, "image/png", "receipt.png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("supported image", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Heic_bytes_are_accepted_at_the_gate()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        // A real HEIC photo (the iPhone default). Since the handler now decodes to downscale, this proves
        // HEIC clears both the magic-byte gate AND the preprocessor's decode/transcode step end-to-end.
        var heic = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Intake", "Fixtures", "receipt-oversized.heic"));

        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, heic, "image/heic", "receipt.heic"));

        // HEIC must clear the magic-byte gate (not a 400 wrong-type rejection).
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((int)response.StatusCode < 400,
            $"HEIC must be admitted past the gates, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Bursting_past_the_limit_returns_429_with_retry_after()
    {
        // Fresh factory so this test owns the singleton limiter's counters; burst of 3.
        using var burstFactory = UploadFragmentFactory.WithBurstLimit(3);
        var (client, token) = await AuthedClientWithTokenAsync(burstFactory);

        // The first three uploads are admitted.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsync(ParseUrl, BuildUpload(token, ValidPng(), "image/png", "r.png"));
            Assert.True((int)ok.StatusCode < 400, $"Upload #{i + 1} should be admitted, got {(int)ok.StatusCode}.");
        }

        // The fourth trips the burst limit.
        var throttled = await client.PostAsync(ParseUrl, BuildUpload(token, ValidPng(), "image/png", "r.png"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"), "A 429 must carry a Retry-After header.");
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_processing()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        // > 10 MB: over the request-level RequestSizeLimit / MultipartBodyLengthLimit cap.
        var oversized = new byte[(10 * 1024 * 1024) + 4096];
        ValidPng().CopyTo(oversized.AsSpan()); // valid header, but the body is too big to buffer

        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, oversized, "image/png", "huge.png"));

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"An oversized upload must be rejected at the request level, got {(int)response.StatusCode}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task<(HttpClient client, string token)> AuthedClientWithTokenAsync(UploadFragmentFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var pageHtml = await (await client.GetAsync(UploadUrl)).Content.ReadAsStringAsync();
        return (client, AntiforgeryToken(pageHtml));
    }

    private static MultipartFormDataContent BuildUpload(string token, byte[] bytes, string contentType, string fileName)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "Receipt", fileName);
        return content;
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the upload page.");
        return match.Groups[1].Value;
    }
}
