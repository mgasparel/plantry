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

    public Task<List<ImportSession>> ListInMonthWindowAsync(
        HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(Sessions
            .Where(s => s.HouseholdId == householdId &&
                        ((s.CreatedAt >= windowStart && s.CreatedAt <= windowEnd) ||
                         (s.CommittedAt is { } committedAt &&
                          committedAt >= windowStart && committedAt <= windowEnd)))
            .ToList());

    public Task<List<ImportSession>> ListHistoryPageAsync(
        HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        Task.FromResult(Sessions
            .Where(s => s.HouseholdId == householdId &&
                        s.Status != ImportStatus.Parsing &&
                        (beforeCreatedAt == null || s.CreatedAt < beforeCreatedAt))
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToList());

    public Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(
        HouseholdId householdId, IReadOnlyCollection<Guid> lineIds, IReadOnlyCollection<Guid> legacyJournalIds,
        CancellationToken ct = default)
    {
        var lineIdSet = lineIds.ToHashSet();
        var journalIdSet = legacyJournalIds.ToHashSet();

        var rows = Sessions
            .Where(s => s.HouseholdId == householdId)
            .SelectMany(s => s.Lines.Select(l => (Session: s, Line: l)))
            .Where(x => lineIdSet.Contains(x.Line.Id.Value) ||
                        (x.Line.JournalId is { } jid && journalIdSet.Contains(jid)))
            .Select(x => new ImportLineProvenanceRow(
                x.Line.Id.Value, x.Session.Id.Value, x.Line.JournalId!.Value,
                x.Session.MerchantText, x.Session.PurchaseDate, x.Session.CreatedAt))
            .ToList();

        return Task.FromResult<IReadOnlyList<ImportLineProvenanceRow>>(rows);
    }
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
    // The dated-as value each lot was stamped with — lets tests assert commit backdating (plantry-yobz).
    public List<DateOnly?> PurchasedAts { get; } = [];
    // The source ref each add was stamped with — lets tests assert the committing line's id flows through
    // to the journal row (receipt-intake-history.md H1).
    public List<Guid?> SourceRefs { get; } = [];
    public int? FailOnCall { get; set; }

    public Task<Guid> AddStockAsync(
        Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, Guid? sourceRef = null, CancellationToken ct = default)
    {
        if (FailOnCall == ProductIds.Count + 1)
            throw new InvalidOperationException("simulated mid-batch stock failure");

        ProductIds.Add(productId);
        PurchasedAts.Add(purchasedAt);
        SourceRefs.Add(sourceRef);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Records each price write (price + the store_id it was handed) and hands back a fresh observation id.</summary>
internal sealed class FakeRecordPricePort : IRecordPricePort
{
    public List<decimal> Prices { get; } = [];
    public List<Guid?> StoreIds { get; } = [];
    public List<string?> MerchantTexts { get; } = [];
    // Quantity + unit the observation is recorded in — lets tests assert pricing stays in the receipt's
    // true unit ($/lb) even when stock committed in each (plantry-1mu).
    public List<decimal> Quantities { get; } = [];
    public List<Guid> UnitIds { get; } = [];

    public Task<Guid> RecordAsync(
        Guid productId, Guid? skuId, decimal price, decimal quantity, Guid unitId,
        string? merchantText, Guid? storeId, Guid sourceRef, DateTimeOffset observedAt, Guid userId, CancellationToken ct = default)
    {
        Prices.Add(price);
        StoreIds.Add(storeId);
        MerchantTexts.Add(merchantText);
        Quantities.Add(quantity);
        UnitIds.Add(unitId);
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
        return Task.FromResult(data ?? new ReviewReferenceData([], [], [], [], []));
    }
}

/// <summary>Records each seeded weight→each conversion so tests can assert the learned factor, its
/// unit anchors, and that non-estimated / weight-kept lines seed nothing (plantry-1mu).</summary>
internal sealed class FakeSeedConversionPort : ISeedConversionPort
{
    public List<(Guid ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor)> Seeds { get; } = [];

    public Task SeedAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        Seeds.Add((productId, fromUnitId, toUnitId, factor));
        return Task.CompletedTask;
    }
}
