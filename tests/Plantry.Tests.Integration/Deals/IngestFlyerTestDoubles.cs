using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Integration.Deals;

/// <summary>Cross-context port fakes for the L3 <see cref="IngestFlyer"/> isolation test — only the Deals
/// persistence is real (RLS-armed); Catalog/Pricing/Flipp/AI seams are stubbed so the test isolates the
/// tenancy behaviour of the ingestion writes.</summary>
internal sealed class StubFlyerSource : IFlyerSource
{
    private readonly Dictionary<string, Queue<FlyerPullResult>> _byRef = new(StringComparer.Ordinal);

    public void Enqueue(string externalRef, FlyerPullResult result)
    {
        if (!_byRef.TryGetValue(externalRef, out var q))
            _byRef[externalRef] = q = new Queue<FlyerPullResult>();
        q.Enqueue(result);
    }

    public Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(string postalCode, string? nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryMerchant>>([]);

    public Task<FlyerPullResult> PullFlyerAsync(string externalRef, string postalCode, CancellationToken ct = default) =>
        Task.FromResult(_byRef.TryGetValue(externalRef, out var q) && q.Count > 0 ? q.Dequeue() : FlyerPullResult.Failed("none"));
}

internal sealed class StubDealMatcher : IDealMatcher
{
    public Task<MatchProposal> MatchAsync(RawDeal deal, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default) =>
        Task.FromResult(MatchProposal.Unmatched());
}

internal sealed class StubCatalogStoreReader : ICatalogStoreReader
{
    public Dictionary<Guid, string> ExternalRefs { get; } = new();

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(ExternalRefs.TryGetValue(storeId, out var r) ? new CatalogStoreInfo(storeId, "Store", r) : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> storeIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

internal sealed class StubCatalogProductReader : ICatalogProductReader
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductCandidate>>([]);
}

internal sealed class StubPriceObservationWriter : IPriceObservationWriter
{
    public Task<Guid> RecordObservationAsync(
        Guid productId, decimal price, decimal? quantity, Guid? unitId, Guid storeId,
        DateOnly validFrom, DateOnly validTo, Guid dealId, Guid? reviewedByUserId,
        DateTimeOffset observedAt, CancellationToken ct = default) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class ArmedTenantContext : ITenantContext
{
    public Guid? HouseholdId { get; private set; }
    public void Set(Guid id) => HouseholdId = id;
}
