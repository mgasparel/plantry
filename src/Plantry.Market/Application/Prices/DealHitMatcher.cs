using Plantry.Market.Domain;

namespace Plantry.Market.Application;

/// <summary>
/// Intake-time deal-hit detection (plantry-j9q4), shared between <see cref="RecordObservationCommand"/>
/// (a fresh purchase observation) and <see cref="RecordAmendedObservationCommand"/> (a purchase-entry
/// amendment that re-derives the unit price and must re-evaluate the match against that new price — a
/// quantity correction that raises the per-unit price above the deal's must clear a stale match, not
/// silently carry the old one forward, since <see cref="PriceObservation"/> rows are immutable after
/// create (ADR-023) and there is no later chance to fix a wrong stamp).
/// <para>
/// Lives entirely inside <c>Plantry.Market.Application</c> — <see cref="Deal"/> and
/// <see cref="PriceObservation"/> are already the same bounded context (ADR-024), so no new Intake→Market
/// port is needed; Intake never references a Deal/Market type, it only ever calls the pre-existing
/// <c>IRecordPricePort</c> (DM-3 discipline preserved by construction, not by a new port mirroring
/// <c>IDealShoppingListWriter</c>). Only a <b>Confirmed</b> deal ever produces a <see cref="PriceSource.Deal"/>
/// row in the first place (<c>ConfirmDeal.RecordDealObservationAsync</c>) — a Pending or Rejected deal has
/// no row to match here, so "rejected/pending never match" holds by construction, not by an extra status
/// check.
/// </para>
/// </summary>
internal static class DealHitMatcher
{
    /// <summary>Relative allowance (plantry-j9q4 acceptance sketch: "small tolerance") on the deal's
    /// <b>per-base-unit</b> price — a flat cent amount would be meaningless at the per-base-unit scale
    /// unit prices are stored at (e.g. $0.00798/g), so the allowance scales with the deal's own price
    /// instead. Absorbs rounding noise between the receipt's derived unit price and the deal's derived
    /// unit price — both are independently rounded outputs of <see cref="IUnitPriceCalculator"/>, not the
    /// same computation.</summary>
    public const decimal DealHitTolerance = 0.01m; // 1% of the deal's unit price

    /// <summary>Only ever attempted for a <see cref="PriceSource.Purchase"/> row with a resolved store and
    /// a normalizable unit price — a Deal or Manual observation is never itself a "purchase" that could
    /// have hit a deal, and a soft-failed unit price (or an unresolved store, e.g. a blank-merchant receipt
    /// line) has nothing reliable to compare against, so no match is attempted rather than guessing.
    /// Returns the matched deal's id (the deal-sourced observation's <c>SourceRef</c>, which
    /// <c>ConfirmDeal.RecordDealObservationAsync</c> always sets to the deal's own id), or null when no
    /// deal is active at that store/date/price for the product.</summary>
    public static async Task<Guid?> FindAsync(
        IPriceObservationRepository repository,
        PriceSource source,
        Guid productId,
        Guid? storeId,
        decimal? unitPrice,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        if (source != PriceSource.Purchase || storeId is not { } resolvedStoreId || unitPrice is not { } purchaseUnitPrice)
            return null;

        // "At-or-below the deal price, small tolerance" is evaluated inside the query (the repository
        // returns the cheapest deal whose unit price qualifies the purchase) — testing the purchase here
        // against only the cheapest deal would silently reject a purchase made at a dearer-but-qualifying
        // deal's price when several confirmed deals are active in the same store/window.
        var observedDate = DateOnly.FromDateTime(observedAt.UtcDateTime);
        var deal = await repository.ActiveDealForPurchaseAsync(
            productId, resolvedStoreId, observedDate, purchaseUnitPrice, DealHitTolerance, ct);
        return deal?.SourceRef;
    }
}
