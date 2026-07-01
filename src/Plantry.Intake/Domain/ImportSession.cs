using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Intake.Domain;

/// <summary>
/// Aggregate root for the AI receipt intake flow. Lifecycle: Parsing → Ready → Committed (happy),
/// or Parsing → Failed, or * → Discarded. Each state transition is validated; invalid transitions
/// return an <see cref="Error"/> rather than throwing.
/// </summary>
public sealed class ImportSession : AggregateRoot<ImportSessionId>
{
    public HouseholdId HouseholdId { get; private set; }
    public ImportSourceType SourceType { get; private set; }
    public Guid UserId { get; private set; }
    public ImportStatus Status { get; private set; }
    public string? MerchantText { get; private set; }
    // ── Receipt metadata (AI-parsed display data — never read on commit, ADR-007) ──
    public string? StoreBranch { get; private set; }
    public DateOnly? PurchaseDate { get; private set; }
    public TimeOnly? PurchaseTime { get; private set; }
    public decimal? Subtotal { get; private set; }
    public decimal? Tax { get; private set; }
    public decimal? Total { get; private set; }
    public string? PaymentDescriptor { get; private set; }
    public string? ReceiptNumber { get; private set; }
    public string? ParseError { get; private set; }
    public DateTimeOffset? ParsedAt { get; private set; }
    public DateTimeOffset? CommittedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private readonly List<ImportLine> _lines = [];
    public IReadOnlyList<ImportLine> Lines => _lines.AsReadOnly();

    private ImportSession() { } // EF

    public static ImportSession Start(HouseholdId householdId, ImportSourceType sourceType, Guid userId, IClock clock) =>
        new()
        {
            Id = ImportSessionId.New(),
            HouseholdId = householdId,
            SourceType = sourceType,
            UserId = userId,
            Status = ImportStatus.Parsing,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

    public ImportLine AddLine(
        int lineNo,
        string receiptText,
        SuggestedConfidence confidence,
        string? rawPayload,
        Guid? suggestedProductId = null,
        string? suggestedProductName = null,
        decimal? suggestedQuantity = null,
        string? suggestedUnitLabel = null,
        decimal? suggestedPrice = null,
        IReadOnlyList<AlternativeCandidate>? suggestedAlternatives = null)
    {
        var line = ImportLine.Create(
            ImportLineId.New(),
            Id,
            HouseholdId,
            lineNo,
            receiptText,
            confidence,
            rawPayload,
            suggestedProductId,
            suggestedProductName,
            suggestedQuantity,
            suggestedUnitLabel,
            suggestedPrice,
            suggestedAlternatives);
        _lines.Add(line);
        return line;
    }

    public Result MarkParsingFailed(string error)
    {
        if (Status != ImportStatus.Parsing)
            return Error.Custom("Intake.InvalidTransition", $"Cannot mark failed from status '{Status}'.");

        ParseError = error;
        Status = ImportStatus.Failed;
        return Result.Success();
    }

    public Result MarkReady(string? merchantText, DateTimeOffset parsedAt, ReceiptMetadata? metadata = null)
    {
        if (Status != ImportStatus.Parsing)
            return Error.Custom("Intake.InvalidTransition", $"Cannot mark ready from status '{Status}'.");

        MerchantText = merchantText;
        if (metadata is not null)
        {
            StoreBranch = metadata.StoreBranch;
            PurchaseDate = metadata.PurchaseDate;
            PurchaseTime = metadata.PurchaseTime;
            Subtotal = metadata.Subtotal;
            Tax = metadata.Tax;
            Total = metadata.Total;
            PaymentDescriptor = metadata.PaymentDescriptor;
            ReceiptNumber = metadata.ReceiptNumber;
        }
        ParsedAt = parsedAt;
        Status = ImportStatus.Ready;
        UpdatedAt = parsedAt;
        return Result.Success();
    }

    public Result MarkCommitted(DateTimeOffset committedAt)
    {
        if (Status != ImportStatus.Ready)
            return Error.Custom("Intake.InvalidTransition", $"Cannot commit from status '{Status}'.");

        CommittedAt = committedAt;
        Status = ImportStatus.Committed;
        UpdatedAt = committedAt;
        RaiseDomainEvent(new ImportSessionCommittedEvent(Id, HouseholdId, committedAt));
        return Result.Success();
    }

    public Result Discard()
    {
        if (Status == ImportStatus.Committed)
            return Error.Custom("Intake.InvalidTransition", "Cannot discard a committed session.");

        Status = ImportStatus.Discarded;
        return Result.Success();
    }
}
