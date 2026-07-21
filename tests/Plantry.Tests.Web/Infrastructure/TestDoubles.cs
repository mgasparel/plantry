using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// In-memory <see cref="IImportSessionRepository"/> for the WAF harness. Crucially it mirrors the real
/// (RLS / EF query-filter) tenant scoping: <see cref="FindAsync"/> only returns a session whose
/// <see cref="ImportSession.HouseholdId"/> matches the ambient tenant — so a household A request cannot read a
/// household B session. The ambient household is supplied by the same scoped <see cref="ITenantContext"/> the
/// page uses, kept consistent through the request.
/// </summary>
public sealed class FakeImportSessionRepository(ITenantContext tenant, ImportSession session)
    : IImportSessionRepository
{
    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default)
    {
        // Tenant scoping: only visible to the owning household (matches RLS + EF query filter behaviour).
        if (tenant.HouseholdId is not { } hid || session.HouseholdId.Value != hid)
            return Task.FromResult<ImportSession?>(null);
        if (session.Id != sessionId)
            return Task.FromResult<ImportSession?>(null);
        return Task.FromResult<ImportSession?>(session);
    }

    // The review snapshot tests never exercise the write path, so the remaining members are inert.
    public Task AddAsync(ImportSession s, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult<ImportReceipt?>(null);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListInMonthWindowAsync(HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListHistoryPageAsync(HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(HouseholdId householdId, IReadOnlyCollection<Guid> lineIds, IReadOnlyCollection<Guid> legacyJournalIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportLineProvenanceRow>>([]);
}

/// <summary>Returns the fixed review reference data (dropdown options) the fragments render against.</summary>
public sealed class FakeReviewReferenceDataProvider(ReviewReferenceData data) : IReviewReferenceDataProvider
{
    public Task<ReviewReferenceData> GetAsync(CancellationToken ct = default) => Task.FromResult(data);
}

/// <summary>
/// A DB-free <see cref="InventoryQueryService"/> for the WAF harness: overrides only the two count
/// methods the Upload "This month" card consumes (<see cref="InventoryQueryService.CountInStockAsync"/>
/// and <see cref="InventoryQueryService.CountExpiringSoonAsync"/>) to return fixed values, so a page GET
/// renders the pantry stats without touching Postgres. The base dependencies are null because no other
/// method is exercised on this page. Register it in place of the concrete service in ConfigureTestServices.
/// </summary>
public sealed class StubInventoryQueryService(int inStock, int expiringSoon)
    : InventoryQueryService(null!, null!, null!, null!, null!, null!)
{
    public override Task<int> CountInStockAsync(CancellationToken ct = default) => Task.FromResult(inStock);
    public override Task<int> CountExpiringSoonAsync(CancellationToken ct = default) => Task.FromResult(expiringSoon);
}
