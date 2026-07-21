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
/// D1 (tidy-up.md §3): flags a product with at least one active stock lot whose unit cannot convert to
/// the product's display (default) unit. This is the exact conversion-failure semantic
/// <see cref="InventoryQueryService.DisplayQuantity"/> and <c>ShoppingPantryReaderAdapter</c> already
/// fall back around (the Onion Yellow "false out" case, plantry-2hfi) — Tidy Up surfaces the underlying
/// data gap those paths quietly paper over.
/// <para>
/// Fingerprint covers only the sorted distinct unconvertible lot unit ids plus the display unit id —
/// <b>not</b> quantities (§4 "fingerprint discipline"): buying more of the same unconvertible unit is
/// the same problem, not a new one, so it must not reopen a dismissed finding.
/// </para>
/// </summary>
public sealed class StockUnitUnconvertibleDetector(
    IProductStockRepository stocks,
    ICatalogReadFacade catalog,
    IProductConversionProvider conversions,
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
                continue; // product archived/removed from catalog — skip, same as the pantry read model

            var converter = convertersByProduct.TryGetValue(stock.ProductId, out var c)
                ? c
                : await conversions.ForProductAsync(stock.ProductId, ct);

            var unconvertibleUnitIds = activeLots
                .Select(l => l.UnitId)
                .Distinct()
                .Where(unitId => converter.Convert(1m, unitId, product.DefaultUnitId).IsFailure)
                .OrderBy(id => id)
                .ToList();

            if (unconvertibleUnitIds.Count == 0)
                continue;

            var unconvertibleQty = activeLots
                .Where(l => unconvertibleUnitIds.Contains(l.UnitId))
                .Sum(l => l.Quantity);
            var representativeUnitCode = unitCodes.GetValueOrDefault(unconvertibleUnitIds[0], "?");

            findings.Add(new Finding(
                Id,
                SubjectId: stock.ProductId,
                SubjectName: product.Name,
                Specifics: $"{FormatQuantity(unconvertibleQty)} {representativeUnitCode} in stock, display unit is {product.DefaultUnitCode}",
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
