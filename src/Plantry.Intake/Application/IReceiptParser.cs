using Plantry.Intake.Domain;

namespace Plantry.Intake.Application;

/// <summary>
/// Port: AI vision pipeline that parses a receipt image into structured line items.
/// Implemented in Plantry.Intake.Infrastructure (Slice 6b).
/// Catalog hints are passed in so the model can suggest product IDs inline — no separate pass.
/// </summary>
public interface IReceiptParser
{
    Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes,
        string contentType,
        IReadOnlyList<ProductHint> catalogHints,
        CancellationToken ct = default);
}

/// <summary>
/// A catalog product offered to the parser as a match candidate. <paramref name="TracksEach"/> is
/// true when the product's canonical stocking unit is a count/each unit (Dimension.Count) — the model
/// uses it to decide whether a weight-priced line (e.g. produce sold by the pound) should also carry an
/// estimated each-count (plantry-1mu). A genuinely weight-tracked product (deli meat, bulk grains) has
/// <c>TracksEach = false</c> and is never converted.
/// </summary>
public sealed record ProductHint(Guid Id, string Name, IReadOnlyList<string> SkuLabels, bool TracksEach = false);

/// <summary>
/// One ranked alternative product candidate from the AI parser for an ambiguous receipt line.
/// Confidence is a numeric value in [0, 1] — best match first (index 0 is highest confidence).
/// The list is <em>extras-only</em>: it excludes whichever candidate the parser promoted to
/// <see cref="ParsedLine.SuggestedProductId"/>. Each entry carries a resolved catalog
/// <see cref="ProductId"/> (never null or free-text) so the UI can resolve it to a product name.
/// </summary>
public sealed record ParsedAlternative(
    Guid? ProductId,
    string ProductName,
    decimal Confidence);

public sealed record ParsedLine(
    int LineNo,
    string ReceiptText,
    string? SuggestedProductName,
    Guid? SuggestedProductId,
    decimal? Quantity,
    string? UnitLabel,
    decimal? Price,
    string? Confidence,
    string? RawJson,
    /// <summary>Ranked alternative product candidates (best-first). Null when only one candidate.</summary>
    IReadOnlyList<ParsedAlternative>? Alternatives = null,
    /// <summary>
    /// LLM-estimated count for a weight-priced line whose matched product is tracked by each
    /// (plantry-1mu, e.g. ~7 bananas from 1.34 lb). Null unless the model both judged the line
    /// weight-priced AND matched it to an each-tracked product; <see cref="Quantity"/> +
    /// <see cref="UnitLabel"/> still carry the ground-truth weight for such a line.
    /// </summary>
    decimal? EstimatedEachCount = null,
    /// <summary>The model's confidence label ("high" | "low") for <see cref="EstimatedEachCount"/>;
    /// gates whether the review drawer pre-fills the count or merely suggests it. Null when no estimate.</summary>
    string? EstimatedEachConfidence = null);

public sealed record ReceiptParseResult(
    string? MerchantText,
    IReadOnlyList<ParsedLine> Lines,
    string? ErrorMessage = null,
    /// <summary>Receipt-header metadata (store branch, date/time, totals, payment, number).
    /// Null on a soft-failed parse; individual fields are null when the AI could not read them.</summary>
    ReceiptMetadata? Metadata = null)
{
    public bool HasError => ErrorMessage is not null;
}
