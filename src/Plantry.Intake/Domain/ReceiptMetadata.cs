namespace Plantry.Intake.Domain;

/// <summary>
/// The receipt-header metadata the AI parses off a scanned receipt, landed on the
/// <see cref="ImportSession"/> at <see cref="ImportSession.MarkReady"/>. Every field is optional —
/// receipts vary, and the parser returns null for anything it cannot read.
///
/// <para>This is <em>untrusted display data</em> (ADR-007): most fields are shown on the review receipt
/// panel but never read by the commit orchestration. The one exception since plantry-yobz is
/// <see cref="PurchaseDate"/> — once the user has reviewed (and may have corrected) it via
/// <see cref="ImportSession.CorrectHeader"/>, the resolved purchase date drives the committed stock lot's
/// dated-as value. The remaining fields exist purely to render the full receipt the user photographed and
/// never cross the ACL boundary into the pantry.</para>
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
