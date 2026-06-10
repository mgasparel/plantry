using Plantry.Intake.Application;

namespace Plantry.Web.Intake;

/// <summary>
/// Deterministic, no-network <see cref="IReceiptParser"/> used only for the end-to-end intake journey
/// test (plantry-zbk). It is registered <em>solely</em> when <c>AI:UseFakeParser=true</c> — the production
/// default is always the real <see cref="Plantry.Intake.Infrastructure.GeminiReceiptParser"/>, which this
/// type never replaces unless that flag is explicitly set (see Program.cs). It requires no API key, so
/// CI/E2E never needs the OpenRouter secret.
///
/// <para>It ignores the image bytes entirely and returns a fixed two-line result that exercises both
/// resolution paths the review form supports:</para>
/// <list type="bullet">
///   <item>a <b>matched</b> line whose <c>SuggestedProductId</c> is the first product in the household's
///   catalog hints (high confidence) — drives the resolve-against-existing path;</item>
///   <item>an <b>unmatched</b> line with a fixed novel name and no suggested id — drives the
///   confirm-as-new path.</item>
/// </list>
/// Both lines carry a price so the commit writes price observations against <see cref="FixedMerchant"/>.
/// </summary>
public sealed class FakeReceiptParser : IReceiptParser
{
    /// <summary>Merchant text the fake always reports — the E2E test asserts price observations by it.</summary>
    public const string FixedMerchant = "E2E Test Mart";

    /// <summary>Receipt text for the unmatched line the test confirms as a brand-new product.</summary>
    public const string UnmatchedReceiptText = "MYSTERY SNACK BAR";

    public Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default)
    {
        var lines = new List<ParsedLine>();

        // Matched line — points at a real catalog product so the review form can resolve it against an
        // existing product. The test seeds exactly one product before uploading, so the first hint is
        // deterministic. If (defensively) there are no hints, this line is omitted and only the
        // confirm-as-new path is exercised.
        if (catalogHints.Count > 0)
        {
            var matched = catalogHints[0];
            lines.Add(new ParsedLine(
                LineNo: 1,
                ReceiptText: $"{matched.Name.ToUpperInvariant()} 1EA",
                SuggestedProductName: matched.Name,
                SuggestedProductId: matched.Id,
                Quantity: 2m,
                UnitLabel: null,
                Price: 3.49m,
                Confidence: "high",
                RawJson: null));
        }

        // Unmatched line — no suggested product, so the user confirms it as a new product (§2d path).
        lines.Add(new ParsedLine(
            LineNo: lines.Count + 1,
            ReceiptText: UnmatchedReceiptText,
            SuggestedProductName: null,
            SuggestedProductId: null,
            Quantity: 1m,
            UnitLabel: null,
            Price: 1.99m,
            Confidence: "none",
            RawJson: null));

        return Task.FromResult(new ReceiptParseResult(FixedMerchant, lines));
    }
}
