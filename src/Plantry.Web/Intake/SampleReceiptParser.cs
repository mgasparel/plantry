using Plantry.Intake.Application;
using Plantry.Intake.Domain;

namespace Plantry.Web.Intake;

/// <summary>
/// Deterministic, no-network <see cref="IReceiptParser"/> for <b>local UI iteration</b> — it lets the
/// upload → review → commit journey run against a realistic, multi-line receipt without spending AI
/// credits. Registered <em>solely</em> when <c>AI:UseSampleParser=true</c> (Development only); the
/// production default is always the real <see cref="Plantry.Intake.Infrastructure.GeminiReceiptParser"/>.
///
/// <para>The fixture below was captured verbatim from a real scanned session
/// (<c>REAL CANADIAN SUPERSTORE</c>, 9 lines, 3 high-confidence matches) so the review form renders the
/// same shape of data the live AI produces. Unlike the E2E <see cref="FakeReceiptParser"/> — which is
/// pinned by <c>ReceiptIntakeJourneyTests</c> and must not change — this parser exists purely as a dev
/// affordance and is free to evolve.</para>
///
/// <para>Matched lines carry a product <em>name</em>, not an id: the seeded catalog uses UUIDv7 keys that
/// change on every re-seed, so the id is resolved by name from the household's <see cref="ProductHint"/>s
/// at parse time. A name that no longer resolves degrades gracefully to a name-only suggestion.</para>
/// </summary>
public sealed class SampleReceiptParser : IReceiptParser
{
    /// <summary>Merchant text from the captured session.</summary>
    public const string Merchant = "REAL CANADIAN SUPERSTORE";

    /// <summary>One captured receipt line. <see cref="SuggestedName"/> non-null marks a high-confidence
    /// match whose catalog id is resolved by name at parse time; null marks an unmatched line.
    /// <see cref="AlternativeNames"/> is an ordered list of candidate names (best-first) with confidence
    /// scores — used to populate the "Did you mean" suggestion block in the review drawer.</summary>
    private sealed record SampleLine(
        int LineNo,
        string ReceiptText,
        string? SuggestedName,
        decimal Quantity,
        string? UnitLabel,
        decimal Price,
        (string Name, decimal Confidence)[]? AlternativeNames = null);

    private static readonly SampleLine[] Lines =
    [
        // Ambiguous match — two extras-only alternatives to demo the "Did you mean" block.
        // AlternativeNames is extras-only (excludes the primary "Butter"), matching the new contract.
        new(1, "05995030018 BECE MARG W-AVOC", "Butter",
            1.000m, null, 7.99m,
            AlternativeNames: [("Margarine", 0.62m), ("Avocado Spread", 0.41m)]),
        new(2, "06038366414 LARGE EGGS",       null,                      1.000m, null, 3.93m),
        new(3, "06148300741 CRANBERRIES",      null,                      1.000m, null, 6.00m),
        new(4, "4012 ORANGE NAVEL LG",         null,                      0.255m, "kg", 1.40m),
        new(5, "4663 ONION WHITE",             "Onions",                  0.230m, "kg", 1.52m),
        new(6, "4664 TOV GH RED",              null,                      0.355m, "kg", 2.34m),
        new(7, "4693 PEP JALEPANO HOT",        null,                      0.040m, "kg", 0.44m),
        new(8, "2278110 SALMON MAPLE",         "Salmon fillet",           1.000m, null, 9.98m),
        new(9, "49 OTHER",                     null,                      1.000m, null, 6.00m),
    ];

    public async Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default)
    {
        // Dev affordance: the fixture resolves instantly, which makes the upload scan-line animation
        // flash past before you can see it. A real AI parse takes a few seconds, so pause here to mimic
        // that latency and let the animation play. Scoped to this sample parser only (Development).
        await Task.Delay(Random.Shared.Next(3000, 5000), ct);

        // name → id, first match wins (the catalog has duplicate "Salmon fillet" rows; either resolves).
        var idByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in catalogHints)
            idByName.TryAdd(hint.Name, hint.Id);

        var parsed = Lines.Select(l =>
        {
            Guid? suggestedId = l.SuggestedName is { } name && idByName.TryGetValue(name, out var id)
                ? id
                : null;

            // Resolve alternative candidate ids by name (same approach as the primary suggestion).
            // Alternatives without a matching catalog id are still included — the island's
            // resolver will filter candidates whose id cannot be verified against the household catalog.
            List<ParsedAlternative>? alternatives = null;
            if (l.AlternativeNames is { Length: >= 2 } altNames)
            {
                alternatives = altNames.Select(a =>
                {
                    Guid? altId = idByName.TryGetValue(a.Name, out var aid) ? aid : (Guid?)null;
                    return new ParsedAlternative(altId, a.Name, a.Confidence);
                }).ToList();
            }

            return new ParsedLine(
                LineNo: l.LineNo,
                ReceiptText: l.ReceiptText,
                SuggestedProductName: l.SuggestedName,
                SuggestedProductId: suggestedId,
                Quantity: l.Quantity,
                UnitLabel: l.UnitLabel,
                Price: l.Price,
                // Confidence tracks resolution, not just intent: a suggested name that resolves to a
                // real catalog id is a "high" match (populates the product dropdown); a name that no
                // longer resolves degrades to "low" (a name-only hint) rather than masquerading as a
                // confident match with an empty dropdown; no suggestion at all is "none".
                Confidence: suggestedId is not null ? "high" : l.SuggestedName is null ? "none" : "low",
                RawJson: null,
                Alternatives: alternatives);
        }).ToList();

        // Captured receipt header/footer so the dev review panel renders the full receipt shape.
        var metadata = new ReceiptMetadata(
            StoreBranch: "1000 Marine Dr, North Vancouver",
            PurchaseDate: new DateOnly(2026, 6, 7),
            PurchaseTime: new TimeOnly(14, 34),
            Subtotal: 39.60m,
            Tax: 1.98m,
            Total: 41.58m,
            PaymentDescriptor: "VISA ****4471 APPROVED",
            ReceiptNumber: "TXN 0472 118 6620");

        return new ReceiptParseResult(Merchant, parsed, Metadata: metadata);
    }
}
