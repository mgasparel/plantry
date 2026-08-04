using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D6 (tidy-up.md §3): flags a product whose active lots span units with no mutual conversion to the
/// product's display unit — exactly the case where <c>InventoryQueryService.DisplayQuantity</c> falls
/// back to its <c>"?"</c> unit code (InventoryQueries.cs, the mixed-incompatible-units fallback). Reuses
/// that exact fallback semantic (reimplemented locally as <see cref="DisplayQuantity"/> over this
/// detector's own already-loaded facts) against the same inputs D1 already assembles (active lots,
/// default unit, a converter, unit codes) instead of reimplementing the convertibility decision — D1 and
/// D6 legitimately can both fire on the same product (D1: <i>any</i> unconvertible lot; D6: <i>all</i>
/// active stock unconvertible to the display unit, so the total itself can't be shown at all).
/// <para>
/// Fingerprint mirrors D1's discipline: sorted distinct active-lot unit ids + the display unit id — never
/// quantities. The inputs are the same shape as D1's; the <see cref="Housekeeping.Domain.DetectorId"/>
/// half of the dismissal key keeps the two detectors' tombstones distinct even when the fingerprint bytes
/// happen to coincide for a given product.
/// </para>
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IStockFactsReadModel"/> (shared with D1/D3/D4)
/// rather than the retired ports — the math below is unchanged from the original port-backed version.
/// </para>
/// </summary>
public sealed class MixedIncompatibleUnitsDetector(
    IStockFactsReadModel factsReadModel,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.StockMixedIncompatibleUnits;
    public Severity Severity => Severity.BehaviorAffecting;
    public string GroupTitle => "Mixed incompatible units in stock";
    public string GroupConsequence =>
        "A product's active lots use units that can't convert to each other — the pantry shows its quantity as \"?\" and consumption order across lots is unreliable.";
    public string IconName => "i-scale";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var bag = await factsReadModel.LoadAsync(ct);
        if (bag.StockByProduct.Count == 0)
            return [];

        var converter = bag.BuildConverter();

        var findings = new List<Finding>();
        foreach (var stock in bag.StockByProduct.Values)
        {
            var activeLots = stock.Entries.Where(l => l.IsActive).ToList();
            if (activeLots.Count == 0)
                continue;
            if (!bag.Products.TryGetValue(stock.ProductId, out var product))
                continue; // product archived/removed from catalog — skip, same as D1

            var defaultUnitCode = bag.Units.TryGetValue(product.DefaultUnitId, out var du) ? du.Code : "?";
            var (_, unitCode) = DisplayQuantity(activeLots, product.DefaultUnitId, defaultUnitCode, converter, stock.ProductId, bag.Units);
            if (unitCode != "?")
                continue;

            var distinctUnitIds = activeLots.Select(l => l.UnitId).Distinct().OrderBy(id => id).ToList();
            var breakdown = string.Join(" + ", activeLots
                .GroupBy(l => l.UnitId)
                .Select(g => (UnitCode: bag.Units.TryGetValue(g.Key, out var u) ? u.Code : "?", Qty: g.Sum(l => l.Quantity)))
                .OrderBy(t => t.UnitCode, StringComparer.Ordinal)
                .Select(t => $"{FormatQuantity(t.Qty)} {t.UnitCode}"));

            findings.Add(new Finding(
                Id,
                SubjectId: stock.ProductId,
                SubjectName: product.Name,
                Specifics: $"{breakdown} in stock — none convert to each other, quantity shows as \"?\"",
                Consequence: "Pantry shows quantity as \"?\" · consumption ordering across lots unreliable",
                FixUrl: $"/Catalog/Products/{stock.ProductId}#conversions",
                FixLabel: "Fix in Catalog",
                FactsFingerprint: Fingerprint(distinctUnitIds, product.DefaultUnitId)));
        }

        return findings;
    }

    /// <summary>
    /// Local reimplementation of <c>InventoryQueryService.DisplayQuantity</c>'s fallback semantic over
    /// this detector's own already-loaded facts (ADR-021 rule 1 — no round-trip inside this method):
    /// sums active lots converted into the default unit; if that total is zero (conversion failed
    /// entirely) falls back to the lots' own unit, or <c>"?"</c> when the active lots span more than one
    /// distinct unit and none converts.
    /// </summary>
    private static (decimal Total, string UnitCode) DisplayQuantity(
        IReadOnlyList<StockLotFact> activeLots, Guid defaultUnitId, string defaultUnitCode,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter, Guid productId,
        IReadOnlyDictionary<Guid, UnitFact> units)
    {
        var total = 0m;
        foreach (var lot in activeLots)
        {
            if (lot.UnitId == defaultUnitId)
            {
                total += lot.Quantity;
                continue;
            }

            var converted = converter(productId, lot.Quantity, lot.UnitId, defaultUnitId);
            if (converted.IsSuccess)
                total += converted.Value;
        }

        if (total > 0 || activeLots.Count == 0)
            return (total, defaultUnitCode);

        var distinctUnitIds = activeLots.Select(l => l.UnitId).Distinct().ToList();
        var fallbackId = distinctUnitIds[0];
        var fallbackCode = units.TryGetValue(fallbackId, out var fu) ? fu.Code : "?";
        return distinctUnitIds.Count == 1
            ? (activeLots.Sum(l => l.Quantity), fallbackCode)
            : (activeLots.Sum(l => l.Quantity), "?"); // mixed incompatible units — honest but rare
    }

    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Sorted distinct active-lot unit ids + the display unit id — never quantities (§4), mirroring
    /// D1's fingerprint discipline exactly.</summary>
    private static string Fingerprint(IReadOnlyList<Guid> unitIds, Guid displayUnitId)
    {
        var raw = string.Join(",", unitIds.Select(id => id.ToString())) + "|" + displayUnitId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
