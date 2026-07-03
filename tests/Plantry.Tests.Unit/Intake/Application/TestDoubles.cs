using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Intake.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>In-memory <see cref="IImportSessionRepository"/>. Saves are inline; the call count lets
/// tests assert the per-line save cadence that makes commit resumable.</summary>
internal sealed class FakeImportSessionRepository : IImportSessionRepository
{
    public List<ImportSession> Sessions { get; } = [];
    public List<ImportReceipt> Receipts { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task AddAsync(ImportSession session, CancellationToken ct = default)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default)
    {
        Receipts.Add(receipt);
        return Task.CompletedTask;
    }

    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.SingleOrDefault(s => s.Id == sessionId));

    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult(Receipts.SingleOrDefault(r => r.Id == sessionId));

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.Where(s => s.HouseholdId == householdId && s.Status == ImportStatus.Ready).ToList());

    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.Any(s => s.HouseholdId == householdId && s.Status == ImportStatus.Ready));

    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(Sessions
            .Where(s => s.HouseholdId == householdId &&
                        (s.Status == ImportStatus.Ready || s.Status == ImportStatus.Committed || s.Status == ImportStatus.Failed))
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToList());
}

/// <summary>Returns a canned parse result (or error) and records the hints it was handed.</summary>
internal sealed class FakeReceiptParser(ReceiptParseResult result) : IReceiptParser
{
    public IReadOnlyList<ProductHint>? ReceivedHints { get; private set; }
    public int Calls { get; private set; }

    public Task<ReceiptParseResult> ParseAsync(
        byte[] imageBytes, string contentType, IReadOnlyList<ProductHint> catalogHints, CancellationToken ct = default)
    {
        Calls++;
        ReceivedHints = catalogHints;
        return Task.FromResult(result);
    }
}

internal sealed class FakeCatalogHintProvider(params ProductHint[] hints) : ICatalogHintProvider
{
    public Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<ProductHint>)hints);
}

/// <summary>Records each create and hands back a fresh product id; can be told to throw.</summary>
internal sealed class FakeCreateProductPort : ICreateProductPort
{
    public List<(string Name, Guid CategoryId, Guid UnitId)> Calls { get; } = [];

    public Task<Guid> CreateAsync(string name, Guid categoryId, Guid defaultUnitId, CancellationToken ct = default)
    {
        Calls.Add((name, categoryId, defaultUnitId));
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Records each add and hands back a fresh journal id; can fail on the Nth call to model a
/// mid-batch failure for the resumability test.</summary>
internal sealed class FakeAddStockPort : IAddStockPort
{
    public List<Guid> ProductIds { get; } = [];
    public int? FailOnCall { get; set; }

    public Task<Guid> AddStockAsync(
        Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, CancellationToken ct = default)
    {
        if (FailOnCall == ProductIds.Count + 1)
            throw new InvalidOperationException("simulated mid-batch stock failure");

        ProductIds.Add(productId);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Records each price write (price + the store_id it was handed) and hands back a fresh observation id.</summary>
internal sealed class FakeRecordPricePort : IRecordPricePort
{
    public List<decimal> Prices { get; } = [];
    public List<Guid?> StoreIds { get; } = [];
    public List<string?> MerchantTexts { get; } = [];

    public Task<Guid> RecordAsync(
        Guid productId, Guid? skuId, decimal price, decimal quantity, Guid unitId,
        string? merchantText, Guid? storeId, Guid sourceRef, DateTimeOffset observedAt, Guid userId, CancellationToken ct = default)
    {
        Prices.Add(price);
        StoreIds.Add(storeId);
        MerchantTexts.Add(merchantText);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Resolves any merchant name to a stable, per-name store id and records each call, so tests can
/// assert the same store is reused across a session's lines (idempotent find-or-create). Can be told to throw.</summary>
internal sealed class FakeEnsurePurchaseStorePort : IEnsurePurchaseStorePort
{
    private readonly Dictionary<string, Guid> _byName = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public bool Throw { get; set; }

    public Task<Guid> EnsureAsync(string merchantName, CancellationToken ct = default)
    {
        Calls.Add(merchantName);
        if (Throw)
            throw new InvalidOperationException("simulated store-ensure failure");

        if (!_byName.TryGetValue(merchantName, out var id))
        {
            id = Guid.CreateVersion7();
            _byName[merchantName] = id;
        }
        return Task.FromResult(id);
    }
}

/// <summary>Returns canned review reference data and records that it was asked.</summary>
internal sealed class FakeReviewReferenceDataProvider(ReviewReferenceData? data = null) : IReviewReferenceDataProvider
{
    public int Calls { get; private set; }

    public Task<ReviewReferenceData> GetAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(data ?? new ReviewReferenceData([], [], [], []));
    }
}
