using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using InvProductStock = Plantry.Inventory.Domain.ProductStock;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Deterministic fixture data for the L4 review-banner fragment tests (plantry-yb6).
///
/// Provides a household with stock and recipes (so IsColdStart=false) plus a configurable
/// set of Ready intake sessions so the banner stack renders.
/// </summary>
public static class TodayReviewBannerFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000099");

    /// <summary>
    /// Builds one Ready intake session with the given number of lines and an optional store name.
    /// Uses a fixed clock offset so tests are deterministic.
    /// </summary>
    public static ImportSession BuildReadySession(
        int lineCount = 3,
        string? store = "Whole Foods",
        int minutesAgo = 30)
    {
        var household = HouseholdId.From(HouseholdAId);
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var clock = new OffsetClock(minutesAgo);

        var session = ImportSession.Start(household, ImportSourceType.Receipt, userId, clock);
        for (var i = 1; i <= lineCount; i++)
            session.AddLine(i, $"Item {i}", SuggestedConfidence.High, null, suggestedPrice: i * 1.50m);

        session.MarkReady(store, clock.UtcNow);
        return session;
    }

    private sealed class OffsetClock(int minutesAgo) : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
    }
}

// ── Fake IImportSessionRepository for the Today L4 banner tests ──────────────

/// <summary>
/// In-memory <see cref="IImportSessionRepository"/> that returns a configurable list of
/// Ready sessions from <see cref="ListPendingAsync"/>. Used by the review-banner fragment tests
/// to exercise one-session, many-session, and no-session states.
/// </summary>
public sealed class FakeBannerSessionRepository(IReadOnlyList<ImportSession> pendingSessions)
    : IImportSessionRepository
{
    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(pendingSessions.Any(s => s.HouseholdId == householdId));

    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(pendingSessions.Where(s => s.HouseholdId == householdId).ToList());

    // Remainder unused by the banner fragment tests.
    public Task AddAsync(ImportSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportSession?>(null);
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListInMonthWindowAsync(HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListHistoryPageAsync(HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(HouseholdId householdId, IReadOnlyCollection<Guid> lineIds, IReadOnlyCollection<Guid> legacyJournalIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportLineProvenanceRow>>([]);
}
