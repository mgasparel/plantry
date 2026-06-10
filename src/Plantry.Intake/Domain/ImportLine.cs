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
    public Guid? SuggestedProductId { get; private set; }
    public string? SuggestedProductName { get; private set; }
    public decimal? SuggestedQuantity { get; private set; }
    public string? SuggestedUnitLabel { get; private set; }
    public decimal? SuggestedPrice { get; private set; }

    // User-resolved fields
    public Guid? ProductId { get; private set; }
    public Guid? SkuId { get; private set; }
    public decimal? Quantity { get; private set; }
    public Guid? UnitId { get; private set; }
    public Guid? LocationId { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }
    public decimal? Price { get; private set; }

    // New-product intent (ADR-010 create-at-commit): when the user resolves an unmatched line to a
    // brand-new product, ProductId stays null and the product is created by CommitSessionCommand — so no
    // orphan product is left behind if the session is never committed. The purchase UnitId doubles as the
    // new product's default unit.
    public string? NewProductName { get; private set; }
    public Guid? NewProductCategoryId { get; private set; }

    /// <summary>A confirmed line whose product does not yet exist — created at commit time.</summary>
    public bool IsNewProduct => ProductId is null && NewProductName is not null;

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
        string? rawParse,
        Guid? suggestedProductId = null,
        string? suggestedProductName = null,
        decimal? suggestedQuantity = null,
        string? suggestedUnitLabel = null,
        decimal? suggestedPrice = null) =>
        new()
        {
            Id = id,
            SessionId = sessionId,
            HouseholdId = householdId,
            LineNo = lineNo,
            ReceiptText = receiptText,
            SuggestedConfidence = confidence,
            RawParse = rawParse,
            SuggestedProductId = suggestedProductId,
            SuggestedProductName = suggestedProductName,
            SuggestedQuantity = suggestedQuantity,
            SuggestedUnitLabel = suggestedUnitLabel,
            SuggestedPrice = suggestedPrice,
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
        // Resolving to an existing product clears any prior new-product intent.
        NewProductName = null;
        NewProductCategoryId = null;
        Status = LineStatus.Confirmed;
        return Result.Success();
    }

    /// <summary>
    /// Confirms the line against a product that does not exist yet (the §2d unmatched create/link path).
    /// The product is created by <c>CommitSessionCommand</c>; <see cref="ProductId"/> stays null until
    /// then. The purchase <paramref name="unitId"/> becomes the new product's default unit.
    /// </summary>
    public Result ConfirmAsNew(
        string newProductName, Guid newProductCategoryId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, decimal? price)
    {
        if (Status == LineStatus.Dismissed)
            return Error.Custom("Intake.LineAlreadyDismissed", "Cannot confirm a dismissed line.");
        if (Status == LineStatus.Committed)
            return Error.Custom("Intake.LineAlreadyCommitted", "Cannot re-confirm an already committed line.");
        if (string.IsNullOrWhiteSpace(newProductName))
            return Error.Custom("Intake.MissingProductName", "A new product needs a name.");

        ProductId = null;
        SkuId = null;
        NewProductName = newProductName.Trim();
        NewProductCategoryId = newProductCategoryId;
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

    /// <summary>
    /// Restores a dismissed line back to <see cref="LineStatus.Pending"/> (the SPEC §2e "Add anyway" action),
    /// so the user can resolve it again. Only a dismissed line can be restored — an already-committed line is
    /// final, and confirming/pending lines have nothing to restore.
    /// </summary>
    public Result Restore()
    {
        if (Status != LineStatus.Dismissed)
            return Error.Custom("Intake.LineNotDismissed", "Only a dismissed line can be restored.");

        Status = LineStatus.Pending;
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
