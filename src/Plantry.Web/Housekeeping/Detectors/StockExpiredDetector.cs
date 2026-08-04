using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Composition.Infrastructure;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// D3 (tidy-up.md §3): flags a product with at least one active stock lot whose
/// <see cref="StockLotFact.ExpiryDate"/> is strictly before today — GRACE WINDOW: 0 days (agreed
/// 2026-07-21): a lot expiring today does not fire, only one whose expiry has already passed. Uses
/// <see cref="IClock"/> for "today" (never the ambient wall clock directly), matching the house
/// convention (e.g. <c>PriceReaderAdapter</c>).
/// <para>
/// Fingerprint is the sorted set of expired lot ids — never quantities (§4 "fingerprint discipline"): a
/// newly-expired lot changes the id set and reopens a dismissed finding; consuming part of an
/// already-expired lot does not.
/// </para>
/// <para>
/// ADR-021/ADR-024 Phase A: loads its facts via <see cref="IStockFactsReadModel"/> (raw cross-schema SQL
/// on an RLS-armed connection) rather than the retired <c>IProductStockRepository</c>/<c>ICatalogReadFacade</c>
/// ports — the math below is unchanged from the original port-backed version.
/// </para>
/// </summary>
public sealed class StockExpiredDetector(
    IStockFactsReadModel factsReadModel,
    IClock clock,
    ITenantContext tenant)
    : IProblemDetector
{
    public DetectorId Id => DetectorId.StockExpired;
    public Severity Severity => Severity.BehaviorAffecting;
    public string GroupTitle => "Expired stock still counted";
    public string GroupConsequence =>
        "An active lot's expiry date has passed — on-hand is inflated, the product may be hidden from shopping suggestions, and meal planning may assume it's usable.";
    public string IconName => "i-clock";

    public async Task<IReadOnlyList<Finding>> DetectAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return [];

        var bag = await factsReadModel.LoadAsync(ct);
        if (bag.StockByProduct.Count == 0)
            return [];

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var findings = new List<Finding>();
        foreach (var stock in bag.StockByProduct.Values)
        {
            var expiredLots = stock.Entries
                .Where(l => l.IsActive && l.ExpiryDate is { } expiry && expiry < today)
                .ToList();
            if (expiredLots.Count == 0)
                continue;
            if (!bag.Products.TryGetValue(stock.ProductId, out var product))
                continue; // product archived/removed from catalog — skip, same as D1/D2

            var oldestExpiry = expiredLots.Min(l => l.ExpiryDate!.Value);
            var specifics = expiredLots.Count == 1
                ? $"1 lot expired {oldestExpiry:yyyy-MM-dd}"
                : $"{expiredLots.Count} lots expired, oldest {oldestExpiry:yyyy-MM-dd}";

            findings.Add(new Finding(
                Id,
                SubjectId: stock.ProductId,
                SubjectName: product.Name,
                Specifics: specifics,
                Consequence: "Inflates on-hand · hides it from shopping suggestions · meal planning assumes it's usable",
                FixUrl: $"/Pantry/Products/Detail/{stock.ProductId}",
                FixLabel: "Review in Pantry",
                FactsFingerprint: Fingerprint(expiredLots.Select(l => l.EntryId))));
        }

        return findings;
    }

    /// <summary>Sorted expired lot ids only — never quantities (§4). A newly-expired lot changes this set
    /// and reopens a dismissed finding; consuming part of an already-expired lot does not.</summary>
    private static string Fingerprint(IEnumerable<Guid> expiredEntryIds)
    {
        var raw = string.Join(",", expiredEntryIds.OrderBy(id => id).Select(id => id.ToString()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
