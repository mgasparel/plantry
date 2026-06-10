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

public sealed record ParsedLine(
    int LineNo,
    string ReceiptText,
    string? SuggestedProductName,
    Guid? SuggestedProductId,
    decimal? Quantity,
    string? UnitLabel,
    decimal? Price,
    string? Confidence,
    string? RawJson);

public sealed record ReceiptParseResult(
    string? MerchantText,
    IReadOnlyList<ParsedLine> Lines,
    string? ErrorMessage = null)
{
    public bool HasError => ErrorMessage is not null;
}
