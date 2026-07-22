using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Pricing.Domain;

/// <summary>
/// Flat, append-only aggregate root. One row per observed price event — the immutable price event
/// (price/quantity/unit/source/observed_at/…) is never edited after creation; it may only become
/// superseded (ADR-023 A7). Two sanctioned late-binds exist, both a one-time set-once-if-null/throw
/// pattern: <see cref="StoreId"/> (a late-resolved soft-reference, bound once via
/// <see cref="ResolveStore"/>, DM-16 backfill) and <see cref="SupersededById"/> (bound exactly once via
/// <see cref="Supersede"/> when a purchase-entry amendment replaces this row with a corrected one,
/// ADR-023 A7 — repeat amendments chain off the live row; <see cref="Supersede"/> throws rather than
/// letting a second amendment fork off an already-superseded row). Every pricing read path filters
/// <c>SupersededById IS NULL</c>, so a superseded row is invisible except to audit/history surfaces that
/// explicitly opt into the full chain. Nothing else on the row is ever updated.
/// <see cref="UnitPrice"/> is null when the calculator could not normalize (soft-fail, pricing.md resolved-call #2).
/// </summary>
public sealed class PriceObservation : AggregateRoot<PriceObservationId>
{
    public HouseholdId HouseholdId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? SkuId { get; private set; }
    public decimal Price { get; private set; }
    public decimal Quantity { get; private set; }
    public Guid UnitId { get; private set; }
    public decimal? UnitPrice { get; private set; }
    public PriceSource Source { get; private set; }
    public string? MerchantText { get; private set; }

    /// <summary>Resolved merchant identity (soft ref → <c>catalog.store</c>). Null for purchases and
    /// until <c>store</c> exists (DM-16); populated by Deals (Phase 5).</summary>
    public Guid? StoreId { get; private set; }

    /// <summary>Deal validity window start (DM-17). Set only for <see cref="PriceSource.Deal"/>;
    /// null for a purchase (a point observation at <see cref="ObservedAt"/>).</summary>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Deal validity window end (DM-17) — drives the "cheapest active deal" read model.
    /// Set only for <see cref="PriceSource.Deal"/>; null for a purchase.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Provenance soft ref to the writer's record (pricing.md): <c>intake.import_line</c>
    /// (purchase) or <c>deals.deal</c> (deal). Null for <see cref="PriceSource.Manual"/> — a
    /// household-entered estimate has no source document to point at (plantry-3fqm).</summary>
    public Guid? SourceRef { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Self-reference to the observation this row amends (ADR-023 A7). Set only on an amending
    /// row created by <see cref="RecordAmendment"/>; null on every ordinary Purchase/Deal/Manual row.</summary>
    public PriceObservationId? AmendsId { get; private set; }

    /// <summary>One-time late bind (ADR-023 A7, same precedent as <see cref="StoreId"/>/<see cref="ResolveStore"/>):
    /// the observation that superseded this row, set exactly once via <see cref="Supersede"/>. Null for a
    /// still-live row. Every pricing read path filters <c>SupersededById IS NULL</c>.</summary>
    public PriceObservationId? SupersededById { get; private set; }

    private PriceObservation() { } // EF

    public static PriceObservation Record(
        HouseholdId householdId,
        Guid productId,
        Guid? skuId,
        decimal price,
        decimal quantity,
        Guid unitId,
        decimal? unitPrice,
        PriceSource source,
        string? merchantText,
        Guid? sourceRef,
        DateTimeOffset observedAt,
        Guid userId,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        Guid? storeId = null) =>
        new()
        {
            Id = PriceObservationId.New(),
            HouseholdId = householdId,
            ProductId = productId,
            SkuId = skuId,
            Price = price,
            Quantity = quantity,
            UnitId = unitId,
            UnitPrice = unitPrice,
            Source = source,
            MerchantText = merchantText,
            StoreId = storeId,
            ValidFrom = validFrom,
            ValidTo = validTo,
            SourceRef = sourceRef,
            ObservedAt = observedAt,
            UserId = userId,
        };

    /// <summary>
    /// One-time DM-16 late-bind of the resolved merchant identity: sets <see cref="StoreId"/> <b>only</b>
    /// when it is currently null, and touches nothing else on the row (the immutable price event is left
    /// intact). A no-op when a store is already resolved, so the backfill sweep is idempotent and
    /// re-runnable. Returns <see langword="true"/> when it bound a store, <see langword="false"/> when the
    /// observation was already resolved.
    /// </summary>
    public bool ResolveStore(Guid storeId)
    {
        if (StoreId is not null)
            return false;

        StoreId = storeId;
        return true;
    }

    /// <summary>
    /// ADR-023 A7/A8: creates the <b>amending</b> row for a purchase-entry quantity correction. Carries
    /// the <b>same</b> <see cref="Price"/>, <see cref="ObservedAt"/>, and <see cref="SourceRef"/> as
    /// <paramref name="original"/> — the price event's time and cost didn't change, only the quantity was
    /// wrong, so <c>ObservedAt</c> is preserved for price-history windows — plus the corrected
    /// <paramref name="correctedQuantity"/> and a freshly re-derived <paramref name="unitPrice"/> (the
    /// caller re-runs the commit-time unit-price derivation with the corrected quantity, A8 — never a
    /// naive scale of the old unit price). <see cref="AmendsId"/> points back to <paramref name="original"/>'s
    /// id, completing one half of the audit pair; the caller must still call <see cref="Supersede"/> on
    /// <paramref name="original"/> to bind the other half.
    /// </summary>
    public static PriceObservation RecordAmendment(
        PriceObservation original,
        decimal correctedQuantity,
        decimal? unitPrice,
        Guid userId) =>
        new()
        {
            Id = PriceObservationId.New(),
            HouseholdId = original.HouseholdId,
            ProductId = original.ProductId,
            SkuId = original.SkuId,
            Price = original.Price,
            Quantity = correctedQuantity,
            UnitId = original.UnitId,
            UnitPrice = unitPrice,
            Source = original.Source,
            MerchantText = original.MerchantText,
            StoreId = original.StoreId,
            ValidFrom = original.ValidFrom,
            ValidTo = original.ValidTo,
            SourceRef = original.SourceRef,
            ObservedAt = original.ObservedAt,
            UserId = userId,
            AmendsId = original.Id,
        };

    /// <summary>
    /// One-time bind of <see cref="SupersededById"/> (ADR-023 A7, same late-bind precedent as
    /// <see cref="ResolveStore"/> DM-16): records that <paramref name="replacementId"/> — the amending row
    /// produced by <see cref="RecordAmendment"/> — has superseded this one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This row is already superseded. Repeat amendments must chain off the live/current row — never fork
    /// a second amendment off a row that has already been replaced.
    /// </exception>
    public void Supersede(PriceObservationId replacementId)
    {
        if (SupersededById is not null)
            throw new InvalidOperationException(
                $"PriceObservation '{Id}' is already superseded by '{SupersededById}' — repeat amendments " +
                "must chain off the live row, not fork off an already-superseded one.");

        SupersededById = replacementId;
    }
}
