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

    /// <summary>
    /// The Catalog <c>store_id</c> the user explicitly picked in review (plantry-yobz), held as a bare
    /// cross-context id (Gate 2: IDs only, never an embedded entity — Intake stays free of Catalog's
    /// <c>StoreId</c> type). When set, commit resolves the purchase store to this id directly instead of
    /// find-or-creating one from <see cref="MerchantText"/>. Null means "no explicit pick" — commit falls
    /// back to the <see cref="MerchantText"/> find-or-create path (covers both the untouched-AI value and a
    /// user-typed "create new" name).
    /// </summary>
    public Guid? SelectedStoreId { get; private set; }

    // ── Receipt metadata (AI-parsed display data) ──
    // Most fields are display-only (ADR-007) and never read on commit; PurchaseDate is the exception since
    // plantry-yobz — the (possibly user-corrected) purchase date drives the committed stock lot's dated-as
    // value. It becomes a user-resolved typed field via CorrectHeader before it crosses the ACL.
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
        IReadOnlyList<AlternativeCandidate>? suggestedAlternatives = null,
        decimal? receiptWeight = null,
        string? receiptWeightUnitLabel = null,
        decimal? estimatedEachCount = null,
        SuggestedConfidence? estimatedEachConfidence = null)
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
            suggestedAlternatives,
            receiptWeight,
            receiptWeightUnitLabel,
            estimatedEachCount,
            estimatedEachConfidence);
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

    /// <summary>
    /// Applies a user correction to the parsed receipt header during review (plantry-yobz) — the intervention
    /// point for a defensible-but-wrong AI extraction (a store minted as "Store #100616", a day/month-swapped
    /// date). Gated to <see cref="ImportStatus.Ready"/>: the header is only correctable while the session is
    /// awaiting review, never after commit. Overwrites the whole header block from the review draft:
    /// <list type="bullet">
    /// <item><paramref name="merchantText"/> — the merchant display name (blank normalizes to null); retained
    /// on the purchase price observation for provenance.</item>
    /// <item><paramref name="selectedStoreId"/> — the explicitly-picked Catalog store id, or null when the user
    /// left the AI value or typed a "create new" name (both resolved from <paramref name="merchantText"/> at
    /// commit). The caller validates the id belongs to the household before passing it.</item>
    /// <item><paramref name="purchaseDate"/> — the corrected purchase date; null leaves the field empty
    /// (commit falls back to commit-time). This is the designated backstop for date misreads.</item>
    /// <item><paramref name="purchaseTime"/> — a display-only correction (no commit consumer today).</item>
    /// </list>
    /// </summary>
    public Result CorrectHeader(
        string? merchantText, Guid? selectedStoreId, DateOnly? purchaseDate, TimeOnly? purchaseTime, IClock clock)
    {
        if (Status != ImportStatus.Ready)
            return Error.Custom("Intake.InvalidTransition", $"Cannot correct the header from status '{Status}'.");

        MerchantText = string.IsNullOrWhiteSpace(merchantText) ? null : merchantText.Trim();
        SelectedStoreId = selectedStoreId;
        PurchaseDate = purchaseDate;
        PurchaseTime = purchaseTime;
        UpdatedAt = clock.UtcNow;
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
