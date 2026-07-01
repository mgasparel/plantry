namespace Plantry.Intake.Domain;

/// <summary>
/// The receipt-header metadata the AI parses off a scanned receipt, landed on the
/// <see cref="ImportSession"/> at <see cref="ImportSession.MarkReady"/>. Every field is optional —
/// receipts vary, and the parser returns null for anything it cannot read.
///
/// <para>This is <em>untrusted display data</em> (ADR-007): it is shown on the review receipt panel
/// but never read by the commit orchestration — nothing here crosses the ACL boundary into the pantry.
/// It exists purely to render the full receipt the user photographed.</para>
/// </summary>
public sealed record ReceiptMetadata(
    string? StoreBranch = null,
    DateOnly? PurchaseDate = null,
    TimeOnly? PurchaseTime = null,
    decimal? Subtotal = null,
    decimal? Tax = null,
    decimal? Total = null,
    string? PaymentDescriptor = null,
    string? ReceiptNumber = null);
