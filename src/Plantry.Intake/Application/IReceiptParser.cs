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

public sealed record ProductHint(Guid Id, string Name, IReadOnlyList<string> SkuLabels);

/// <summary>
/// One ranked alternative product candidate from the AI parser for an ambiguous receipt line.
/// Confidence is in [0, 1] — the parser may normalise values differently but must keep index 0 as
/// the best match (highest confidence). The first candidate is the same as SuggestedProductId on
/// <see cref="ParsedLine"/> when the parser emits both (they are kept in sync by convention).
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
    IReadOnlyList<ParsedAlternative>? Alternatives = null);

public sealed record ReceiptParseResult(
    string? MerchantText,
    IReadOnlyList<ParsedLine> Lines,
    string? ErrorMessage = null)
{
    public bool HasError => ErrorMessage is not null;
}
