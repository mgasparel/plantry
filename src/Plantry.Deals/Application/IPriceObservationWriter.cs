namespace Plantry.Deals.Application;

/// <summary>
/// Port onto Pricing's observation write seam (deals-domain-model §7/§8, ADR-010, DM-17). When a deal is
/// confirmed/corrected, <c>ConfirmDeal</c> projects its advertised sale price into
/// <c>pricing.price_observation</c> as a <b>source=deal</b> row carrying the validity window, the
/// <c>store_id</c>, and <c>source_ref = deal_id</c> — the append-only shape P5-P built.
/// <para>
/// Deals never touches <c>PricingDbContext</c> directly (ADR-010/DM-3); the Web adapter wraps Pricing's
/// <c>RecordObservationCommand</c>, mirroring intake's <c>RecordPriceAdapter</c>. It <b>throws only on a
/// hard failure</b> so a per-deal commit can abort that deal cleanly. Returns the created
/// <c>price_observation</c> id as a <see cref="Guid"/> for <c>Deal.LinkObservation</c>.
/// </para>
/// </summary>
public interface IPriceObservationWriter
{
    /// <summary>
    /// Records a <b>new</b> deal-sourced price observation (a confirm, or an append-only supersede on a
    /// correction — never an edit, DM-17/R1). <paramref name="quantity"/>/<paramref name="unitId"/> may be
    /// null when the flyer did not advertise a pack size; the unit price soft-fails to null in that case,
    /// but the observation is still recorded (DM-17).
    /// </summary>
    Task<Guid> RecordObservationAsync(
        Guid productId,
        decimal price,
        decimal? quantity,
        Guid? unitId,
        Guid storeId,
        DateOnly validFrom,
        DateOnly validTo,
        Guid dealId,
        Guid? reviewedByUserId,
        DateTimeOffset observedAt,
        CancellationToken ct = default);
}
