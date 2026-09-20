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
/// <para>
/// Parent fold (plantry-oh27.3): a variant with no rule of its own is never evaluated on its own —
/// its stock facts are folded into its parent's group (grouped by <see cref="ProductFact.ParentProductId"/>),
/// and the union of the group's purchase dates decides whether the finding fires, targeting the PARENT
/// (matches the epic rule: intent — including "we buy this often" — lives at the parent when no variant
/// opts out with its own rule). A variant WITH its own rule stays its own group and is evaluated exactly
/// as before. A leaf with no parent is unaffected (its own group is itself, same as pre-plantry-oh27.3).
/// </para>
/// </summary>
public sealed class StapleNoLowStockAlertDetector(
    IStockFactsReadModel factsReadModel,
    IClock clock,
    ITenantContext tenant)
    : IProblemDetector
{
    private const int MinDistinctPurchaseDates = FrequentStaplePredicate.MinimumDistinctPurchaseDates;
    private const int LookbackDays = FrequentStaplePredicate.LookbackDays;

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

        // Fold every variant without its own rule into its parent's group (plantry-oh27.3); a variant
        // WITH its own rule, or a leaf with no parent, is its own group — same shape GetFrequentStapleProductsAsync
        // uses on the Shopping side (ShoppingPantryReaderAdapter).
        var groups = bag.StockByProduct.Values.GroupBy(stock => GroupKey(stock.ProductId, bag));

        var findings = new List<Finding>();
        foreach (var group in groups)
        {
            var groupId = group.Key;
            if (bag.LowStockThresholds.ContainsKey(groupId))
                continue; // the group's own product (parent or leaf) already has a threshold

            if (!bag.Products.TryGetValue(groupId, out var groupProduct))
                continue; // product archived/removed from catalog — skip, same as D1/D3

            var purchaseDates = group.SelectMany(stock => stock.Entries.Select(e => e.PurchasedAt));
            if (!FrequentStaplePredicate.IsFrequent(purchaseDates, today))
                continue;
            var distinctPurchaseDates = purchaseDates
                .Where(d => d is { } date && date >= cutoff)
                .Select(d => d!.Value)
                .Distinct()
                .Count();

            findings.Add(new Finding(
                Id,
                SubjectId: groupId,
                SubjectName: groupProduct.Name,
                Specifics: $"Purchased on {distinctPurchaseDates} separate occasions in the last {LookbackDays} days, no low-stock alert set",
                Consequence: "Never appears in \"Running low\" — only surfaces once fully out",
                FixUrl: $"/Pantry/Products/Detail/{groupId}",
                FixLabel: "Set alert in Pantry",
                FactsFingerprint: ConstantFingerprint));
        }

        return findings;
    }

    /// <summary>Group key for the parent fold: a product with its own low-stock rule stays its own group
    /// (it is filtered out immediately below since it has a threshold); otherwise a variant's group is its
    /// parent, and a parentless product's group is itself.</summary>
    private static Guid GroupKey(Guid productId, StockFactsBag bag) =>
        bag.LowStockThresholds.ContainsKey(productId)
            ? productId
            : bag.Products.TryGetValue(productId, out var product) && product.ParentProductId is { } parentId
                ? parentId
                : productId;

    /// <summary>Constant per subject (§4): the gap is binary — a threshold exists or it doesn't — so
    /// dismissal is permanent. Setting then clearing a threshold deliberately does not reopen this finding.</summary>
    private static readonly string ConstantFingerprint =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("d4-staple-no-low-stock-alert")));
}
