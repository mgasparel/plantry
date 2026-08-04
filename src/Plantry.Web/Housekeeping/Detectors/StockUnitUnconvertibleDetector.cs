using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D1 (tidy-up.md §3): flags a product with at least one active stock lot whose unit cannot convert to
/// the product's display (default) unit. This is the exact conversion-failure semantic
/// <c>InventoryQueryService.DisplayQuantity</c> and <c>ShoppingPantryReaderAdapter</c> already fall back
/// around (the Onion Yellow "false out" case, plantry-2hfi) — Tidy Up surfaces the underlying data gap
/// those paths quietly paper over.
/// <para>
/// Fingerprint covers only the sorted distinct unconvertible lot unit ids plus the display unit id —
/// <b>not</b> quantities (§4 "fingerprint discipline"): buying more of the same unconvertible unit is
/// the same problem, not a new one, so it must not reopen a dismissed finding.
/// </para>
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IStockFactsReadModel"/> and runs the
/// conversion check through the shared <c>Plantry.Pantry.Domain.UnitConverter</c> delegate (see
/// <c>HousekeepingConversions.BuildConverter</c>) rather than the retired
/// <c>IProductStockRepository</c>/<c>ICatalogReadFacade</c>/<c>IProductConversionProvider</c> ports — the
/// math below is unchanged from the original port-backed version.
/// </para>
/// </summary>
public sealed class StockUnitUnconvertibleDetector(
    IStockFactsReadModel factsReadModel,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.StockUnitUnconvertible;
    public Severity Severity => Severity.BehaviorAffecting;
    public string GroupTitle => "Missing unit conversions";
    public string GroupConsequence =>
        "Stock recorded in a unit the product can't convert — quantities may show wrong or as \"out\" until you add a conversion.";
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
                continue; // product archived/removed from catalog — skip, same as the pantry read model

            var unconvertibleUnitIds = activeLots
                .Select(l => l.UnitId)
                .Distinct()
                .Where(unitId => converter(stock.ProductId, 1m, unitId, product.DefaultUnitId).IsFailure)
                .OrderBy(id => id)
                .ToList();

            if (unconvertibleUnitIds.Count == 0)
                continue;

            var defaultUnitCode = bag.Units.TryGetValue(product.DefaultUnitId, out var du) ? du.Code : "?";
            var breakdown = string.Join(" + ", activeLots
                .Where(l => unconvertibleUnitIds.Contains(l.UnitId))
                .GroupBy(l => l.UnitId)
                .Select(g => (UnitCode: bag.Units.TryGetValue(g.Key, out var u) ? u.Code : "?", Qty: g.Sum(l => l.Quantity)))
                .OrderBy(t => t.UnitCode, StringComparer.Ordinal)
                .Select(t => $"{FormatQuantity(t.Qty)} {t.UnitCode}"));

            findings.Add(new Finding(
                Id,
                SubjectId: stock.ProductId,
                SubjectName: product.Name,
                Specifics: $"{breakdown} in stock, display unit is {defaultUnitCode}",
                Consequence: "Shopping may show it as \"out\" · low-stock alert can't trigger",
                FixUrl: $"/Catalog/Products/{stock.ProductId}#conversions",
                FixLabel: "Fix in Catalog",
                FactsFingerprint: Fingerprint(unconvertibleUnitIds, product.DefaultUnitId)));
        }

        return findings;
    }

    private static string FormatQuantity(decimal quantity) =>
        quantity.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Sorted distinct unconvertible lot unit ids + the display unit id — never quantities (§4). The
    /// fingerprint is what makes reopen-on-fact-change work: adding a differently-unconvertible unit,
    /// or changing the product's default unit, changes this hash; buying more of an already-unconvertible
    /// unit does not.
    /// </summary>
    private static string Fingerprint(IReadOnlyList<Guid> unconvertibleUnitIds, Guid displayUnitId)
    {
        var raw = string.Join(",", unconvertibleUnitIds.Select(id => id.ToString())) + "|" + displayUnitId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
