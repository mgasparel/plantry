namespace Plantry.Pricing.Domain;

/// <summary>
/// The three writers of <see cref="PriceObservation"/> rows (pricing.md source taxonomy, ADR-010).
/// <see cref="Manual"/> is a household-entered estimate (plantry-3fqm) — for costing purposes it is
/// treated the same as <see cref="Purchase"/> (see <c>IPriceObservationRepository.LatestForProductAsync</c>/
/// <c>LatestForSkuAsync</c>), but it is never eligible for the DM-16 store-backfill sweep
/// (<c>ListPurchasesAwaitingStoreAsync</c> stays <see cref="Purchase"/>-only) since a manual entry has no
/// merchant to resolve a store from.
/// </summary>
public enum PriceSource { Purchase, Deal, Manual }

public static class PriceSourceExtensions
{
    public static string ToDbValue(this PriceSource source) => source switch
    {
        PriceSource.Purchase => "Purchase",
        PriceSource.Deal => "Deal",
        PriceSource.Manual => "Manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static PriceSource Parse(string value) => value switch
    {
        "Purchase" => PriceSource.Purchase,
        "Deal" => PriceSource.Deal,
        "Manual" => PriceSource.Manual,
        _ => throw new ArgumentException($"Unknown price source '{value}'.", nameof(value)),
    };
}
