namespace Plantry.Deals.Domain;

/// <summary>
/// The <c>IFlyerSource</c> adapter's stage-1 output per flyer item (N4) — the raw advertised fields
/// before normalization/matching. A <b>transient value object, not a table</b>: it is normalized into a
/// <see cref="NormalizedName"/> and materialized into a <see cref="Deal"/>; its raw form survives only
/// inside <see cref="FlyerImport"/>.<c>RawFlyer</c>. The deal-side twin of an Intake stage-1 line item.
/// </summary>
/// <param name="RawName">The item as advertised — the review row's anchor.</param>
/// <param name="Brand">Advertised brand, if any.</param>
/// <param name="Size">Advertised pack/size text, if any.</param>
/// <param name="Price">The advertised sale price for <paramref name="Quantity"/>.</param>
/// <param name="Quantity">Pack size the price is for (for unit_price normalization in Pricing), if known.</param>
/// <param name="UnitId">Soft-ref → catalog.unit; the unit of <paramref name="Quantity"/>, if resolved.</param>
/// <param name="SaleStory">"2 for $5" / "Save $1.50" — free-text provenance (N9).</param>
/// <param name="Window">The flyer's run dates, copied onto the deal (D9).</param>
public sealed record RawDeal(
    string RawName,
    string? Brand,
    string? Size,
    decimal Price,
    decimal? Quantity,
    Guid? UnitId,
    string? SaleStory,
    ValidityWindow Window);
