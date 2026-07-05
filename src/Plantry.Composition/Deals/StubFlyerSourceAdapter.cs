using Plantry.Deals.Application;

namespace Plantry.Web.Deals;

/// <summary>
/// P5-2 placeholder for <see cref="IFlyerSource"/>. Returns a small, deterministic canned merchant
/// directory so the §7e subscribe flow (and its E2E) is stable without the fragile real Flipp source.
/// <see cref="PullFlyerAsync"/> is intentionally unimplemented — the P5-6 worker needs it and the real
/// Flipp adapter (P5-3) supplies both halves of the port, swapping this DI registration out. This stub
/// is a development/placeholder seam and never the live production source.
/// </summary>
public sealed class StubFlyerSourceAdapter : IFlyerSource
{
    // A fixed, deterministic directory so the directory search + E2E are stable (D1: the real source is
    // postal-code-scoped and fragile — validated manually in P5-3).
    private static readonly DirectoryMerchant[] Canned =
    [
        new("flipp-freshco", "FreshCo"),
        new("flipp-loblaws", "Loblaws"),
        new("flipp-metro", "Metro"),
        new("flipp-nofrills", "No Frills"),
        new("flipp-sobeys", "Sobeys"),
        new("flipp-walmart", "Walmart"),
    ];

    public Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default)
    {
        // A real Flipp directory search requires a postal code; the stub honours the same contract.
        if (string.IsNullOrWhiteSpace(postalCode))
            return Task.FromResult<IReadOnlyList<DirectoryMerchant>>([]);

        IReadOnlyList<DirectoryMerchant> results = string.IsNullOrWhiteSpace(nameQuery)
            ? Canned
            : Canned
                .Where(m => m.Name.Contains(nameQuery.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        return Task.FromResult(results);
    }

    public Task<FlyerPullResult> PullFlyerAsync(
        string externalRef, string postalCode, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "The P5-2 stub flyer source does not pull flyers; the real Flipp adapter arrives in P5-3.");
}
