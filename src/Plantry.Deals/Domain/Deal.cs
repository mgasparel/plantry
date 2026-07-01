using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Aggregate root (§5 / DL-O1): one normalized, reviewable deal. Its <b>own</b> long-lived root —
/// browsed while active, feeding Pricing across its window, expiring on its own clock — not a child of
/// <see cref="FlyerImport"/>. <b>Retained</b> as price history (D9).
/// <para>
/// The raw flyer fields and the match proposal are the <b>ACL quarantine</b> — read-only after parse
/// (DD6). Only <see cref="ProductId"/>/<see cref="Status"/> ever commit (DD1). Cross-aggregate effects
/// (writing a price observation, upserting <see cref="DealMatchMemory"/>) are the application service's
/// job — this root keeps its own state pure.
/// </para>
/// </summary>
public sealed class Deal : AggregateRoot<DealId>
{
    public static readonly Error NotResolvable =
        Error.Custom("Deals.Deal.NotResolvable", "Only a Pending deal, or a Confirmed auto-match, can be resolved.");

    public static readonly Error AlreadyRejected =
        Error.Custom("Deals.Deal.AlreadyRejected", "A Rejected deal cannot be re-resolved.");

    public static readonly Error NotConfirmed =
        Error.Custom("Deals.Deal.NotConfirmed", "Only a Confirmed deal can link a price observation.");

    private Deal() { } // EF

    private Deal(
        DealId id, HouseholdId householdId, FlyerImportId? flyerImportId, Guid storeId, DealSource source,
        string rawName, string? brand, string? size, decimal price, decimal? quantity, Guid? unitId,
        string? saleStory, string normalizedName, MatchProposal matchProposal, ValidityWindow window,
        DateTimeOffset now)
        : base(id)
    {
        HouseholdId = householdId;
        FlyerImportId = flyerImportId;
        StoreId = storeId;
        Source = source;
        RawName = rawName;
        Brand = brand;
        Size = size;
        Price = price;
        Quantity = quantity;
        UnitId = unitId;
        SaleStory = saleStory;
        NormalizedName = normalizedName;
        SuggestedProductId = matchProposal.SuggestedProductId;
        MatchConfidence = matchProposal.Confidence;
        MatchReasoning = matchProposal.Reasoning;
        Status = DealStatus.Pending;
        ValidityWindow = window;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Within-context composite FK → <see cref="FlyerImport"/> (RESTRICT). Null only for the deferred manual path (D12).</summary>
    public FlyerImportId? FlyerImportId { get; private set; }

    /// <summary>Soft-ref → catalog.store (denormalized from the import).</summary>
    public Guid StoreId { get; private set; }

    public DealSource Source { get; private set; }

    // ── Raw flyer fields (ACL, read-only after parse) ─────────────────────────────
    public string RawName { get; private set; } = string.Empty;
    public string? Brand { get; private set; }
    public string? Size { get; private set; }
    public decimal Price { get; private set; }
    public decimal? Quantity { get; private set; }
    public Guid? UnitId { get; private set; }
    public string? SaleStory { get; private set; }

    /// <summary>The deterministic key (DD4/DL-O6); with <see cref="StoreId"/> the memory lookup key.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    // ── Match proposal (ACL quarantine, never overwritten — DD6) ───────────────────
    public Guid? SuggestedProductId { get; private set; }
    public MatchConfidence MatchConfidence { get; private set; }
    public string? MatchReasoning { get; private set; }

    // ── User-resolved (the only field that commits) ────────────────────────────────
    /// <summary>The resolved match; set on Confirm/Correct. Null while Pending or after Rejected (DD1).</summary>
    public Guid? ProductId { get; private set; }

    // ── Lifecycle & linkage ────────────────────────────────────────────────────────
    public DealStatus Status { get; private set; }
    public ValidityWindow ValidityWindow { get; private set; } = null!;

    /// <summary>Soft-ref → pricing.price_observation; the row this deal projected on confirm (DD2).</summary>
    public Guid? CommittedPriceObservationId { get; private set; }

    /// <summary>True if memory auto-confirmed it (drives the "auto-matched" marker, DL-O3).</summary>
    public bool AutoMatched { get; private set; }

    /// <summary>Who confirmed/corrected/rejected; null for memory auto-confirm.</summary>
    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Factory. Materializes a <see cref="DealStatus.Pending"/> deal from a raw item + its match proposal (DJ2 step 5).</summary>
    public static Deal Stage(
        HouseholdId householdId,
        FlyerImportId? flyerImportId,
        Guid storeId,
        RawDeal raw,
        NormalizedName normalizedName,
        MatchProposal matchProposal,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(matchProposal);
        if (string.IsNullOrWhiteSpace(raw.RawName))
            throw new ArgumentException("Raw name must not be blank.", nameof(raw));

        return new Deal(
            DealId.New(), householdId, flyerImportId, storeId, DealSource.Flyer,
            raw.RawName, raw.Brand, raw.Size, raw.Price, raw.Quantity, raw.UnitId,
            raw.SaleStory, normalizedName.Value, matchProposal, raw.Window, clock.UtcNow);
    }

    /// <summary>
    /// Memory path (D4): confirms to the remembered product with no reviewer. Sets
    /// <see cref="MatchConfidence"/> to High and flags <see cref="AutoMatched"/>. The observation write
    /// + memory upsert are the service's job (§7).
    /// </summary>
    public Result AutoConfirm(Guid productId, IClock clock)
    {
        if (Status != DealStatus.Pending)
            return NotResolvable;

        ProductId = productId;
        Status = DealStatus.Confirmed;
        AutoMatched = true;
        MatchConfidence = MatchConfidence.High;
        Stamp(clock);
        return Result.Success();
    }

    /// <summary>
    /// User confirms the (possibly corrected) match (DD1). Permitted even when the window has closed —
    /// an explicit price-history backfill (DD14). Valid only from <see cref="DealStatus.Pending"/>.
    /// </summary>
    public Result Confirm(Guid productId, Guid by, IClock clock)
    {
        if (Status == DealStatus.Rejected)
            return AlreadyRejected;
        if (Status != DealStatus.Pending)
            return NotResolvable;

        ProductId = productId;
        Status = DealStatus.Confirmed;
        Review(by, clock);
        return Result.Success();
    }

    /// <summary>
    /// Re-resolves to a different product (DJ4 edge). Valid on a <see cref="DealStatus.Pending"/> deal
    /// <b>or</b> an already-<see cref="DealStatus.Confirmed"/> deal; keeps it Confirmed with the new
    /// <see cref="ProductId"/>. The supersede-observation + memory-rewrite are the service's job.
    /// </summary>
    public Result Correct(Guid productId, Guid by, IClock clock)
    {
        if (Status == DealStatus.Rejected)
            return AlreadyRejected;

        ProductId = productId;
        Status = DealStatus.Confirmed;
        AutoMatched = false;
        Review(by, clock);
        return Result.Success();
    }

    /// <summary>Rejects the deal (DD1): clears <see cref="ProductId"/>. Writes no observation (D5).</summary>
    public Result Reject(Guid by, IClock clock)
    {
        if (Status == DealStatus.Rejected)
            return Result.Success();

        Status = DealStatus.Rejected;
        ProductId = null;
        Review(by, clock);
        return Result.Success();
    }

    /// <summary>Records the committed observation id after the service writes the Pricing row (DD2).</summary>
    public Result LinkObservation(Guid priceObservationId, IClock clock)
    {
        if (Status != DealStatus.Confirmed)
            return NotConfirmed;

        CommittedPriceObservationId = priceObservationId;
        Stamp(clock);
        return Result.Success();
    }

    private void Review(Guid by, IClock clock)
    {
        ReviewedByUserId = by;
        ReviewedAt = clock.UtcNow;
        Stamp(clock);
    }

    private void Stamp(IClock clock) => UpdatedAt = clock.UtcNow;
}
