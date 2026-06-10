using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Domain;

/// <summary>
/// Child entity of <see cref="ImportSession"/>. AI-populated fields are set once at creation and
/// never overwritten (ACL invariant on <see cref="RawParse"/>). User-resolved fields are mutable
/// until the line is committed.
/// </summary>
public sealed class ImportLine : Entity<ImportLineId>
{
    // Set once by AI — never overwritten
    public ImportSessionId SessionId { get; private set; }
    public HouseholdId HouseholdId { get; private set; }
    public int LineNo { get; private set; }
    public string ReceiptText { get; private set; } = string.Empty;
    public SuggestedConfidence SuggestedConfidence { get; private set; }
    public string? RawParse { get; private set; }

    // User-resolved fields
    public Guid? ProductId { get; private set; }
    public Guid? SkuId { get; private set; }
    public decimal? Quantity { get; private set; }
    public Guid? UnitId { get; private set; }
    public Guid? LocationId { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }
    public decimal? Price { get; private set; }

    public LineStatus Status { get; private set; }

    // Linkage written by CommitSessionCommand
    public Guid? JournalId { get; private set; }
    public Guid? PriceObservationId { get; private set; }
    public Guid? CreatedProductId { get; private set; }

    private ImportLine() { } // EF

    internal static ImportLine Create(
        ImportLineId id,
        ImportSessionId sessionId,
        HouseholdId householdId,
        int lineNo,
        string receiptText,
        SuggestedConfidence confidence,
        string? rawParse) =>
        new()
        {
            Id = id,
            SessionId = sessionId,
            HouseholdId = householdId,
            LineNo = lineNo,
            ReceiptText = receiptText,
            SuggestedConfidence = confidence,
            RawParse = rawParse,
            Status = LineStatus.Pending,
        };

    public Result Confirm(
        Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, decimal? price)
    {
        if (Status == LineStatus.Dismissed)
            return Error.Custom("Intake.LineAlreadyDismissed", "Cannot confirm a dismissed line.");
        if (Status == LineStatus.Committed)
            return Error.Custom("Intake.LineAlreadyCommitted", "Cannot re-confirm an already committed line.");

        ProductId = productId;
        SkuId = skuId;
        Quantity = quantity;
        UnitId = unitId;
        LocationId = locationId;
        ExpiryDate = expiryDate;
        Price = price;
        Status = LineStatus.Confirmed;
        return Result.Success();
    }

    public Result Dismiss()
    {
        if (Status == LineStatus.Committed)
            return Error.Custom("Intake.LineAlreadyCommitted", "Cannot dismiss an already committed line.");

        Status = LineStatus.Dismissed;
        return Result.Success();
    }

    public Result MarkCommitted(Guid journalId, Guid? priceObservationId, Guid? createdProductId = null)
    {
        if (Status != LineStatus.Confirmed)
            return Error.Custom("Intake.LineNotConfirmed", "Only confirmed lines can be committed.");

        JournalId = journalId;
        PriceObservationId = priceObservationId;
        CreatedProductId = createdProductId;
        Status = LineStatus.Committed;
        return Result.Success();
    }
}
