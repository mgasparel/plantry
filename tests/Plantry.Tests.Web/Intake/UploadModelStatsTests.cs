using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Identity.Application;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web;
using Plantry.Web.Intake;
using Plantry.Web.Pages.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L1 composition tests for <see cref="UploadModel.OnGetAsync"/> wiring the "This month" card
/// (plantry-bzyr): the real <see cref="GetMonthlyIntakeStatsQuery"/> runs over a seeded in-memory
/// session repository and the Inventory count queries are stubbed, proving the page maps every stat
/// onto its view property. Covers the populated month, the empty month (zeros / null footer), and the
/// no-household case (defaults, no throw). The clock is pinned mid-month so timestamps stay clear of
/// any day/month boundary on any CI timezone.
/// </summary>
public sealed class UploadModelStatsTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid UserId = Guid.CreateVersion7();

    // Pinned "now": mid-month, noon UTC — robust to any server offset.
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "OnGetAsync — populated month maps every stat onto its view property")]
    public async Task OnGetAsync_PopulatedMonth_MapsAllStats()
    {
        var clock = new MutableClock(Now);

        // A receipt scanned + committed this month: $482.19, parse→commit = 2m 40s.
        var committed = Committed(
            clock,
            createdAt: new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
            parsedAt: new DateTimeOffset(2026, 3, 12, 10, 0, 0, TimeSpan.Zero),
            committedAt: new DateTimeOffset(2026, 3, 12, 10, 2, 40, TimeSpan.Zero),
            total: 482.19m);

        var repo = new SeededSessionRepository(committed);
        var model = BuildModel(repo, clock, HouseholdGuid, inStock: 12, expiringSoon: 3);

        clock.Set(Now); // building the session advanced the clock; the page reads "now" = mid-month.
        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(1, model.ReceiptsScanned);
        Assert.Equal(482.19m, model.GroceriesTotal);
        Assert.Equal(TimeSpan.FromSeconds(160), model.AverageReviewTime);
        Assert.Equal(12, model.ItemsInPantry);
        Assert.Equal(3, model.ExpiringSoonCount);
    }

    [Fact(DisplayName = "OnGetAsync — empty month renders zeros and a null review-time footer")]
    public async Task OnGetAsync_EmptyMonth_ZerosAndNullFooter()
    {
        var clock = new MutableClock(Now);
        var repo = new SeededSessionRepository(/* no sessions */);
        var model = BuildModel(repo, clock, HouseholdGuid, inStock: 0, expiringSoon: 0);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(0, model.ReceiptsScanned);
        Assert.Equal(0m, model.GroceriesTotal);
        Assert.Null(model.AverageReviewTime);
        Assert.Equal(0, model.ItemsInPantry);
        Assert.Equal(0, model.ExpiringSoonCount);

        // The formatters render the empty state exactly as the acceptance criteria describe.
        Assert.Equal("$0.00", UploadModel.FormatMoney(model.GroceriesTotal, model.DisplayCurrency));
        Assert.Equal("—", UploadModel.FormatReviewTime(model.AverageReviewTime));
    }

    [Fact(DisplayName = "OnGetAsync — pantry counts are point-in-time, independent of the month")]
    public async Task OnGetAsync_PantryCounts_IndependentOfMonth()
    {
        // No committed/scanned sessions this month, yet the pantry holds stock — the counts must still show.
        var clock = new MutableClock(Now);
        var repo = new SeededSessionRepository(/* no sessions */);
        var model = BuildModel(repo, clock, HouseholdGuid, inStock: 9, expiringSoon: 2);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(0, model.ReceiptsScanned);
        Assert.Equal(9, model.ItemsInPantry);
        Assert.Equal(2, model.ExpiringSoonCount);
    }

    [Fact(DisplayName = "OnGetAsync — no household leaves stats at their defaults without throwing")]
    public async Task OnGetAsync_NoHousehold_Defaults()
    {
        var clock = new MutableClock(Now);
        var repo = new SeededSessionRepository();
        var model = BuildModel(repo, clock, householdId: null, inStock: 5, expiringSoon: 5);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Empty(model.RecentIntakes);
        Assert.Equal(0, model.ReceiptsScanned);
        Assert.Equal(0m, model.GroceriesTotal);
        Assert.Null(model.AverageReviewTime);
        Assert.Equal(0, model.ItemsInPantry);
        Assert.Equal(0, model.ExpiringSoonCount);
    }

    // ── Builders / doubles ────────────────────────────────────────────────────

    private static UploadModel BuildModel(
        IImportSessionRepository sessions, IClock clock, Guid? householdId, int inStock, int expiringSoon)
    {
        var tenant = new ConstantTenant(householdId);
        var inventory = new StubInventoryQueryService(inStock, expiringSoon);

        // OnGetAsync only touches sessions, clock, tenant, the inventory service and the display currency;
        // the remaining dependencies are inert here (exercised only by the POST/parse handlers).
        return new UploadModel(
            sessions,
            parser: null!,
            hints: null!,
            clock,
            tenant,
            inventory,
            uploadRateLimiter: null!,
            imagePreprocessor: null!,
            displayCurrency: new DisplayCurrencyAccessor(new ConstantDisplayCurrency("USD")),
            logger: NullLogger<UploadModel>.Instance,
            parseLogger: NullLogger<ParseSessionCommand>.Instance);
    }

    private sealed class ConstantDisplayCurrency(string currency) : IDisplayCurrency
    {
        public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult(currency);
    }

    private static ImportSession Committed(
        MutableClock clock, DateTimeOffset createdAt, DateTimeOffset parsedAt,
        DateTimeOffset committedAt, decimal? total)
    {
        clock.Set(createdAt);
        var s = ImportSession.Start(HouseholdId.From(HouseholdGuid), ImportSourceType.Receipt, UserId, clock);
        s.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null);
        s.MarkReady("Store", parsedAt, total is null ? null : new ReceiptMetadata(Total: total));
        s.MarkCommitted(committedAt);
        return s;
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Set(DateTimeOffset value) => UtcNow = value;
    }

    private sealed class ConstantTenant(Guid? householdId) : ITenantContext
    {
        public Guid? HouseholdId { get; } = householdId;
    }

    /// <summary>
    /// In-memory session repository seeded with a fixed set of sessions. Only the read methods the Upload
    /// GET exercises are meaningful; <see cref="ListInMonthWindowAsync"/> returns the sessions whose
    /// <c>CreatedAt</c> OR <c>CommittedAt</c> falls in the window (mirroring the EF repo's union predicate)
    /// so the real query applies the status/null semantics. The write path is inert.
    /// </summary>
    private sealed class SeededSessionRepository(params ImportSession[] sessions) : IImportSessionRepository
    {
        private readonly List<ImportSession> _sessions = [.. sessions];

        public Task<List<ImportSession>> ListInMonthWindowAsync(
            HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
            Task.FromResult(_sessions
                .Where(s =>
                    (s.CreatedAt >= windowStart && s.CreatedAt <= windowEnd) ||
                    (s.CommittedAt is { } c && c >= windowStart && c <= windowEnd))
                .ToList());

        public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
            Task.FromResult(_sessions.OrderByDescending(s => s.CreatedAt).Take(take).ToList());

        public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
            Task.FromResult(_sessions.FirstOrDefault(s => s.Id == sessionId));
        public Task AddAsync(ImportSession s, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
            Task.FromResult<ImportReceipt?>(null);
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(new List<ImportSession>());
        public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
