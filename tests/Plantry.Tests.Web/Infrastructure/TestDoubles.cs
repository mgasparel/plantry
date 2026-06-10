using Plantry.Intake.Application;
using Plantry.Intake.Domain;
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
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
}

/// <summary>Returns the fixed review reference data (dropdown options) the fragments render against.</summary>
public sealed class FakeReviewReferenceDataProvider(ReviewReferenceData data) : IReviewReferenceDataProvider
{
    public Task<ReviewReferenceData> GetAsync(CancellationToken ct = default) => Task.FromResult(data);
}
