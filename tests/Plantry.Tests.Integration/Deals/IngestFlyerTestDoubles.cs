using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Integration.Deals;

/// <summary>A controllable clock so a test can stamp two subscriptions with strictly ordered CreatedAt
/// values, pinning the order <see cref="IngestFlyer"/> processes them (ListActiveAsync orders by CreatedAt).</summary>
internal sealed class MutableClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// Wraps a real <see cref="IDealRepository"/> and makes the <b>first</b> <see cref="SaveChangesAsync"/>
/// throw — a stand-in for a DB-infrastructure fault mid-cycle (plantry-60p9) — then delegates every later
/// call so the next subscription commits against the real, shared context. Every other member delegates
/// unchanged, so the real change tracker (and <see cref="DiscardStagedChanges"/>) behave exactly as in
/// production: the faulted save leaves this subscription's Added/Deleted deals tracked on the shared context.
/// </summary>
internal sealed class FaultOnceDealRepository(IDealRepository inner) : IDealRepository
{
    public bool HasFaulted { get; private set; }

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) => inner.FindAsync(id, ct);

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) => inner.ListBrowsableAsync(ct);

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        inner.ListByFlyerImportAsync(flyerImportId, ct);

    public Task AddAsync(Deal deal, CancellationToken ct = default) => inner.AddAsync(deal, ct);

    public void Remove(Deal deal) => inner.Remove(deal);

    public void DiscardStagedChanges() => inner.DiscardStagedChanges();

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (!HasFaulted)
        {
            HasFaulted = true;
            throw new InvalidOperationException("simulated deals.SaveChangesAsync infrastructure fault (plantry-60p9)");
        }
        return inner.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Wraps a real <see cref="IDealRepository"/> and throws <see cref="OperationCanceledException"/> on the
/// <paramref name="abortOnCall"/>th <see cref="SaveChangesAsync"/> — the in-process stand-in for a HARD
/// CRASH / abort mid-materialize (OOM/eviction/power-loss), NOT a parse exception (plantry-pwkm). OCE is the
/// faithful signal: <see cref="IngestFlyer"/> excludes it from the Failed-recording path, so nothing is
/// recorded and the enclosing transaction rolls back — exactly the hard-crash contract. Every other member
/// delegates, so the real shared change tracker behaves precisely as in production.
/// </summary>
internal sealed class AbortOnDealsSaveDealRepository(IDealRepository inner, int abortOnCall = 1) : IDealRepository
{
    private int _calls;
    public bool HasAborted { get; private set; }

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) => inner.FindAsync(id, ct);

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) => inner.ListBrowsableAsync(ct);

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        inner.ListByFlyerImportAsync(flyerImportId, ct);

    public Task AddAsync(Deal deal, CancellationToken ct = default) => inner.AddAsync(deal, ct);

    public void Remove(Deal deal) => inner.Remove(deal);

    public void DiscardStagedChanges() => inner.DiscardStagedChanges();

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (++_calls == abortOnCall)
        {
            HasAborted = true;
            throw new OperationCanceledException("simulated hard crash / abort mid-materialize (plantry-pwkm)");
        }
        return inner.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Wraps a real <see cref="IDealRepository"/> and, on the first materialize <see cref="SaveChangesAsync"/>,
/// throws a WRAPPER exception whose inner exception carries the real cause — the shape of a
/// <c>DbUpdateException</c> ("An error occurred while saving the entity changes…") over a specific driver/DB
/// message. Proves the Failed-recording path persists the base-exception message in <c>error_detail</c>, not
/// the useless wrapper (plantry-cegw defect 1). Later calls delegate so recovery can commit.
/// </summary>
internal sealed class WrappedFaultDealRepository(IDealRepository inner) : IDealRepository
{
    public const string WrapperMessage = "An error occurred while saving the entity changes. See the inner exception for details.";
    public const string RootCauseMessage = "null value in column \"valid_from\" of relation \"deal\" violates not-null constraint";

    public bool HasFaulted { get; private set; }

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) => inner.FindAsync(id, ct);

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) => inner.ListBrowsableAsync(ct);

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        inner.ListByFlyerImportAsync(flyerImportId, ct);

    public Task AddAsync(Deal deal, CancellationToken ct = default) => inner.AddAsync(deal, ct);

    public void Remove(Deal deal) => inner.Remove(deal);

    public void DiscardStagedChanges() => inner.DiscardStagedChanges();

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (!HasFaulted)
        {
            HasFaulted = true;
            throw new InvalidOperationException(WrapperMessage, new InvalidOperationException(RootCauseMessage));
        }
        return inner.SaveChangesAsync(ct);
    }
}

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
    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DealProductInfo>>(new Dictionary<Guid, DealProductInfo>());
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
