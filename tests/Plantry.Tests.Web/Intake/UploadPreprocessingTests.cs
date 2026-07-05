using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ImageMagick;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 boundary assertions for receipt-image preprocessing (plantry-v8vw), exercised through the real
/// <c>Plantry.Web</c> pipeline via <see cref="UploadFragmentFactory"/>. The unit tests
/// (<see cref="ReceiptImagePreprocessorTests"/>) pin the transcode maths; these prove the handler wiring:
///   1. Bytes that pass the magic-byte gate but cannot be decoded surface the structured aij 400 fragment
///      (not an unhandled 500) — the "undecodable image" acceptance criterion at the HTTP boundary.
///   2. A valid oversized image is admitted past the preprocessing step (downscaled, not rejected).
/// </summary>
public sealed class UploadPreprocessingTests(UploadFragmentFactory factory) : IClassFixture<UploadFragmentFactory>
{
    private const string UploadUrl = "/Intake/Upload";
    private const string ParseUrl = "/Intake/Upload?handler=Parse";

    [Fact]
    public async Task Undecodable_bytes_with_a_valid_prefix_return_the_structured_400_fragment()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        // Valid JPEG SOI marker (clears the magic-byte gate) followed by non-image garbage: the decode
        // inside the preprocessor fails, which must render the aij 400 fragment rather than crashing.
        byte[] corrupt = new byte[512];
        corrupt[0] = 0xFF; corrupt[1] = 0xD8; corrupt[2] = 0xFF;
        for (var i = 3; i < corrupt.Length; i++) corrupt[i] = (byte)(i * 7);

        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, corrupt, "image/jpeg", "receipt.jpg"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Razor HTML-encodes the apostrophe in "couldn't", so match on a substring without one.
        Assert.Contains("read that image", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_oversized_jpeg_is_admitted_past_preprocessing()
    {
        var (client, token) = await AuthedClientWithTokenAsync(factory);

        // 3000x2000 JPEG (> 2048 longest edge): the preprocessor downscales it and the upload proceeds.
        // With the no-key parser it lands on the soft-fail fragment (200) — the point is it was admitted.
        var oversized = MakeJpeg(3000, 2000);
        var response = await client.PostAsync(ParseUrl,
            BuildUpload(token, oversized, "image/jpeg", "big.jpg"));

        Assert.True((int)response.StatusCode < 400,
            $"A valid oversized JPEG must be admitted past preprocessing, got {(int)response.StatusCode}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static byte[] MakeJpeg(uint width, uint height)
    {
        using var image = new MagickImage(new MagickColor("white"), width, height);
        image.Format = MagickFormat.Jpeg;
        image.Quality = 90;
        return image.ToByteArray();
    }

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
