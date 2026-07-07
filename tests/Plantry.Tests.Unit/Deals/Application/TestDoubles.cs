using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Deals.Application;

internal sealed class FakeStoreSubscriptionRepository : IStoreSubscriptionRepository
{
    public List<StoreSubscription> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<StoreSubscription?> FindAsync(StoreSubscriptionId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id));

    public Task<StoreSubscription?> FindByStoreAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.StoreId == storeId));

    public Task<List<StoreSubscription>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.OrderBy(s => s.CreatedAt).ToList());

    public Task<List<StoreSubscription>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(s => s.IsActive).OrderBy(s => s.CreatedAt).ToList());

    public Task AddAsync(StoreSubscription subscription, CancellationToken ct = default)
    {
        Items.Add(subscription);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fake ICatalogStoreWriter that mimics Catalog's ensure-by-external-identity: one stable store id per
/// external_ref, idempotent across calls. Records the ensured merchants for assertions.
/// </summary>
internal sealed class FakeCatalogStoreWriter : ICatalogStoreWriter
{
    private readonly Dictionary<string, Guid> _byRef = new(StringComparer.Ordinal);
    public List<(string ExternalRef, string Name)> EnsureCalls { get; } = [];

    public Task<Guid> EnsureAsync(string externalRef, string name, CancellationToken ct = default)
    {
        EnsureCalls.Add((externalRef, name));
        if (!_byRef.TryGetValue(externalRef, out var id))
        {
            id = Guid.NewGuid();
            _byRef[externalRef] = id;
        }
        return Task.FromResult(id);
    }
}

internal sealed class FakeCatalogStoreReader : ICatalogStoreReader
{
    public Dictionary<Guid, string> Names { get; } = new();

    /// <summary>Store id → Flipp external ref, so the ingest worker can resolve a merchant to pull.</summary>
    public Dictionary<Guid, string?> ExternalRefs { get; } = new();

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Names.TryGetValue(storeId, out var n)
            ? new CatalogStoreInfo(storeId, n, ExternalRefs.GetValueOrDefault(storeId))
            : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = storeIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}

internal sealed class FakeFlyerSource : IFlyerSource
{
    public List<DirectoryMerchant> Merchants { get; } =
    [
        new("flipp-freshco", "FreshCo"),
        new("flipp-metro", "Metro"),
    ];

    public Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return Task.FromResult<IReadOnlyList<DirectoryMerchant>>([]);

        IReadOnlyList<DirectoryMerchant> results = string.IsNullOrWhiteSpace(nameQuery)
            ? Merchants
            : Merchants.Where(m => m.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(results);
    }

    public Task<FlyerPullResult> PullFlyerAsync(
        string externalRef, string postalCode, CancellationToken ct = default) =>
        throw new NotSupportedException("Fake flyer source does not pull.");
}

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>In-memory <see cref="IDealRepository"/>. Saves are inline; the call count lets tests assert
/// the per-mutation save cadence that makes the confirm/reject commit resumable.</summary>
internal sealed class FakeDealRepository : IDealRepository
{
    public List<Deal> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(d => d.Id == id));

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(d => d.Status is DealStatus.Pending or DealStatus.Confirmed)
            .ToList());

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(d => d.FlyerImportId == flyerImportId).ToList());

    public Task AddAsync(Deal deal, CancellationToken ct = default)
    {
        Items.Add(deal);
        return Task.CompletedTask;
    }

    public void Remove(Deal deal) => Items.Remove(deal);

    /// <summary>No-op: this fake commits inline on Add/Remove (no deferred change tracker to reset). The
    /// save-fault isolation the real reset guards (plantry-60p9) is proven against real EF in
    /// <c>IngestFlyerIsolationTests</c>, where a genuine change tracker strands entities on a failed save.</summary>
    public void DiscardStagedChanges() { }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="IDealMatchMemoryRepository"/> keyed on (store, normalized_name).</summary>
internal sealed class FakeDealMatchMemoryRepository : IDealMatchMemoryRepository
{
    public List<DealMatchMemory> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<DealMatchMemory?> FindByKeyAsync(Guid storeId, string normalizedName, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(m => m.StoreId == storeId && m.NormalizedName == normalizedName));

    public Task AddAsync(DealMatchMemory memory, CancellationToken ct = default)
    {
        Items.Add(memory);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>A recorded deal-observation write, for asserting the append-only supersede behaviour.</summary>
internal sealed record RecordedObservation(
    Guid Id, Guid ProductId, decimal Price, decimal? Quantity, Guid? UnitId, Guid StoreId,
    DateOnly ValidFrom, DateOnly ValidTo, Guid DealId, Guid? ReviewedByUserId);

/// <summary>
/// Records each deal-observation write and hands back a fresh id. <see cref="ThrowOnCall"/> models a
/// mid-confirm failure (throw before a row is recorded) so the resumability test can prove a re-drive
/// links only the missing piece without double-writing.
/// </summary>
internal sealed class FakePriceObservationWriter : IPriceObservationWriter
{
    public List<RecordedObservation> Observations { get; } = [];
    public int? ThrowOnCall { get; set; }
    private int _calls;

    public Task<Guid> RecordObservationAsync(
        Guid productId, decimal price, decimal? quantity, Guid? unitId, Guid storeId,
        DateOnly validFrom, DateOnly validTo, Guid dealId, Guid? reviewedByUserId,
        DateTimeOffset observedAt, CancellationToken ct = default)
    {
        _calls++;
        if (ThrowOnCall == _calls)
            throw new InvalidOperationException("simulated deal-observation write failure");

        var id = Guid.CreateVersion7();
        Observations.Add(new RecordedObservation(
            id, productId, price, quantity, unitId, storeId, validFrom, validTo, dealId, reviewedByUserId));
        return Task.FromResult(id);
    }
}

/// <summary>Fake product-existence check; records the ids validated. <see cref="Exists"/> toggles the verdict.</summary>
internal sealed class FakeCatalogProductReader : ICatalogProductReader
{
    public bool Exists { get; set; } = true;
    public List<Guid> Checked { get; } = [];
    public List<ProductCandidate> Candidates { get; } = [];
    public int ListCandidatesCalls { get; private set; }

    /// <summary>Product id → (name, category) resolved by the batch <see cref="ForProductsAsync"/> read.</summary>
    public Dictionary<Guid, DealProductInfo> Products { get; } = new();

    /// <summary>The id sets passed to <see cref="ForProductsAsync"/>, for asserting a single batch call (no N+1).</summary>
    public List<IReadOnlyList<Guid>> ForProductsCalls { get; } = [];

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default)
    {
        Checked.Add(productId);
        return Task.FromResult(Exists);
    }

    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default)
    {
        ListCandidatesCalls++;
        return Task.FromResult<IReadOnlyList<ProductCandidate>>(Candidates.ToList());
    }

    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        ForProductsCalls.Add(productIds);
        IReadOnlyDictionary<Guid, DealProductInfo> result = productIds
            .Where(Products.ContainsKey)
            .ToDictionary(id => id, id => Products[id]);
        return Task.FromResult(result);
    }
}

/// <summary>In-memory <see cref="IFlyerImportRepository"/> keyed on the (store, flyer_external_id) dedup key.</summary>
internal sealed class FakeFlyerImportRepository : IFlyerImportRepository
{
    public List<FlyerImport> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    // Mirrors the partial unique index (plantry-0l05): only Parsed rows occupy the dedup key, so the fake filters
    // Status == Parsed too — otherwise retry-behaviour unit tests would pass falsely against a Failed row.
    public Task<FlyerImport?> FindParsedByDedupKeyAsync(Guid storeId, string flyerExternalId, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(
            f => f.StoreId == storeId && f.FlyerExternalId == flyerExternalId && f.Status == PullStatus.Parsed));

    public Task AddAsync(FlyerImport import, CancellationToken ct = default)
    {
        Items.Add(import);
        return Task.CompletedTask;
    }

    /// <summary>No-op: this fake tracks no EF state, so there is no phantom Unchanged envelope to detach.</summary>
    public void Detach(FlyerImport import) { }

    /// <summary>Runs the action inline — this fake commits on Add/Save with no real transaction. The atomic
    /// commit-or-rollback contract is proven against real Postgres in <c>IngestFlyerAtomicMaterializeTests</c>
    /// (plantry-pwkm), where a genuine transaction actually rolls a partial write back.</summary>
    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default) =>
        action(ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Configurable <see cref="IDealMatcher"/> stand-in: returns a queued proposal per raw name, else
/// <see cref="MatchProposal.Unmatched"/>, positionally aligned with the input batch. Records each deal it
/// was asked to match (in order) for assertions, and each batch call it received.
/// </summary>
internal sealed class FakeDealMatcher : IDealMatcher
{
    public Dictionary<string, MatchProposal> ByRawName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    /// <summary>Number of batch calls (completions from the app's view) — one per <see cref="MatchBatchAsync"/>.</summary>
    public int BatchCalls { get; private set; }

    public Task<IReadOnlyList<MatchProposal>> MatchBatchAsync(
        IReadOnlyList<RawDeal> deals, IReadOnlyList<ProductCandidate> candidates, CancellationToken ct = default)
    {
        BatchCalls++;
        var results = new MatchProposal[deals.Count];
        for (var i = 0; i < deals.Count; i++)
        {
            Calls.Add(deals[i].RawName);
            results[i] = ByRawName.GetValueOrDefault(deals[i].RawName, MatchProposal.Unmatched());
        }
        return Task.FromResult<IReadOnlyList<MatchProposal>>(results);
    }
}

/// <summary>
/// Controllable <see cref="IFlyerSource"/> for ingest tests: a queue of pull results per external ref,
/// so a test can script a first pull then a re-pull (changed / identical / failed). Directory search is
/// unused here.
/// </summary>
internal sealed class FakeIngestFlyerSource : IFlyerSource
{
    private readonly Dictionary<string, Queue<FlyerPullResult>> _byRef = new(StringComparer.Ordinal);
    public List<string> PullCalls { get; } = [];

    public void EnqueuePull(string externalRef, FlyerPullResult result)
    {
        if (!_byRef.TryGetValue(externalRef, out var q))
            _byRef[externalRef] = q = new Queue<FlyerPullResult>();
        q.Enqueue(result);
    }

    public Task<IReadOnlyList<DirectoryMerchant>> SearchDirectoryAsync(
        string postalCode, string? nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryMerchant>>([]);

    public Task<FlyerPullResult> PullFlyerAsync(string externalRef, string postalCode, CancellationToken ct = default)
    {
        PullCalls.Add(externalRef);
        if (_byRef.TryGetValue(externalRef, out var q) && q.Count > 0)
            return Task.FromResult(q.Dequeue());
        return Task.FromResult(FlyerPullResult.Failed("no scripted result"));
    }
}
