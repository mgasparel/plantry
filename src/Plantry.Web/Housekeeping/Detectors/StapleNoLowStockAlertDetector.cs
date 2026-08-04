using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D4 (tidy-up.md §3): flags a product bought often enough to be a staple — purchased on at least
/// <see cref="MinDistinctPurchaseDates"/> distinct <see cref="StockLotFact.PurchasedAt"/> dates within
/// the last <see cref="LookbackDays"/> days — that has no low-stock threshold set. The numbers (§6 open
/// question 1, resolved 2026-07-21 with the owner): ≥3 distinct purchase dates, 90-day lookback from
/// today (resolved via the injected <see cref="IClock"/>, never the ambient wall clock).
/// <para>
/// Counts every stock lot on the product — active <b>and</b> depleted, since frequency is about purchase
/// history, not current stock — whose <see cref="StockLotFact.PurchasedAt"/> is non-null and falls
/// within the window; entries with a null <c>PurchasedAt</c> are ignored (no date to count).
/// </para>
/// <para>
/// Fingerprint is constant per subject (§4): the gap is binary (a threshold is set or it isn't), so
/// dismissal is permanent — setting a threshold and later clearing it deliberately stays dismissed.
/// </para>
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IStockFactsReadModel"/> (shared with
/// D1/D3/D6) rather than the retired <c>IProductStockRepository</c>/<c>ICatalogReadFacade</c> ports —
/// the math below is unchanged from the original port-backed version.
/// </para>
/// </summary>
public sealed class StapleNoLowStockAlertDetector(
    IStockFactsReadModel factsReadModel,
    IClock clock,
    ITenantContext tenant)
    : IProblemDetector
{
    private const int MinDistinctPurchaseDates = 3;
    private const int LookbackDays = 90;

    public DetectorId Id => DetectorId.StapleNoLowStockAlert;
    public Severity Severity => Severity.Advisory;
    public string GroupTitle => "Frequent staples with no low-stock alert";
    public string GroupConsequence =>
        "Bought often but no low-stock threshold is set — it never appears in \"Running low,\" only once it's fully out.";
    public string IconName => "i-alert";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var bag = await factsReadModel.LoadAsync(ct);
        if (bag.StockByProduct.Count == 0)
            return [];

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var cutoff = today.AddDays(-LookbackDays);

        var findings = new List<Finding>();
        foreach (var stock in bag.StockByProduct.Values)
        {
            if (stock.LowStockThreshold is not null)
                continue;

            var distinctPurchaseDates = stock.Entries
                .Where(e => e.PurchasedAt is { } purchasedAt && purchasedAt >= cutoff)
                .Select(e => e.PurchasedAt!.Value)
                .Distinct()
                .Count();
            if (distinctPurchaseDates < MinDistinctPurchaseDates)
                continue;
            if (!bag.Products.TryGetValue(stock.ProductId, out var product))
                continue; // product archived/removed from catalog — skip, same as D1/D3

            findings.Add(new Finding(
                Id,
                SubjectId: stock.ProductId,
                SubjectName: product.Name,
                Specifics: $"Purchased on {distinctPurchaseDates} separate occasions in the last {LookbackDays} days, no low-stock alert set",
                Consequence: "Never appears in \"Running low\" — only surfaces once fully out",
                FixUrl: $"/Pantry/Products/Detail/{stock.ProductId}",
                FixLabel: "Set alert in Pantry",
                FactsFingerprint: ConstantFingerprint));
        }

        return findings;
    }

    /// <summary>Constant per subject (§4): the gap is binary — a threshold exists or it doesn't — so
    /// dismissal is permanent. Setting then clearing a threshold deliberately does not reopen this finding.</summary>
    private static readonly string ConstantFingerprint =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("d4-staple-no-low-stock-alert")));
}
