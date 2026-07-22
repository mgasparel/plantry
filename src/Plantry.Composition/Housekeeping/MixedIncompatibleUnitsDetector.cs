using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Plantry.Housekeeping.Application;
using Plantry.Housekeeping.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D6 (tidy-up.md §3): flags a product whose active lots span units with no mutual conversion to the
/// product's display unit — exactly the case where <see cref="InventoryQueryService.DisplayQuantity"/>
/// falls back to its <c>"?"</c> unit code (InventoryQueries.cs, the mixed-incompatible-units fallback). Reuses
/// that exact method against the same inputs D1 already assembles (active lots, default unit, a
/// converter, unit codes) instead of reimplementing the convertibility decision — D1 and D6 legitimately
/// can both fire on the same product (D1: <i>any</i> unconvertible lot; D6: <i>all</i> active stock
/// unconvertible to the display unit, so the total itself can't be shown at all).
/// <para>
/// Fingerprint mirrors D1's discipline: sorted distinct active-lot unit ids + the display unit id — never
/// quantities. The inputs are the same shape as D1's; the <see cref="Housekeeping.Domain.DetectorId"/>
/// half of the dismissal key keeps the two detectors' tombstones distinct even when the fingerprint bytes
/// happen to coincide for a given product.
/// </para>
/// </summary>
public sealed class MixedIncompatibleUnitsDetector(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
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
        if (tenant.HouseholdId is not { } householdGuid)
            return [];

        var householdId = HouseholdId.From(householdGuid);
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        if (allStock.Count == 0)
            return [];

        var catalogByProduct = (await catalog.ListProductsAsync(ct)).ToDictionary(p => p.Id);
        var unitCodes = await catalog.GetUnitCodesAsync(ct);
        var convertersByProduct = await conversions.ForProductsAsync(allStock.Select(s => s.ProductId), ct);

        var findings = new List<Finding>();
        foreach (var stock in allStock)
        {
            var activeLots = stock.ActiveLotsFefo().ToList();
            if (activeLots.Count == 0)
                continue;
            if (!catalogByProduct.TryGetValue(stock.ProductId, out var product))
                continue; // product archived/removed from catalog — skip, same as D1

            var converter = convertersByProduct.TryGetValue(stock.ProductId, out var c)
                ? c
                : await conversions.ForProductAsync(stock.ProductId, ct);

            var (_, unitCode) = InventoryQueryService.DisplayQuantity(
                activeLots, product.DefaultUnitId, product.DefaultUnitCode, converter, unitCodes);
            if (unitCode != "?")
                continue;

            var distinctUnitIds = activeLots.Select(l => l.UnitId).Distinct().OrderBy(id => id).ToList();
            var breakdown = string.Join(" + ", activeLots
                .GroupBy(l => l.UnitId)
                .Select(g => (UnitCode: unitCodes.GetValueOrDefault(g.Key, "?"), Qty: g.Sum(l => l.Quantity)))
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
