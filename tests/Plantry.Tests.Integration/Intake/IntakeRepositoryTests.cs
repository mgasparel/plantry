using Microsoft.EntityFrameworkCore;
using Plantry.Intake.Domain;
using Plantry.Intake.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Intake;

/// <summary>
/// L3 integration tests proving <see cref="ImportSession"/> with its <see cref="ImportLine"/>
/// children and associated <see cref="ImportReceipt"/> round-trips through EF, and that RLS
/// prevents cross-household reads.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IntakeRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private readonly Guid _userId = Guid.CreateVersion7();
    private static readonly IClock Clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "ImportSession round-trips with lines through EF")]
    public async Task ImportSession_RoundTrips_With_Lines()
    {
        ImportSessionId sessionId;

        await using (var ctx = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            session.AddLine(1, "500g Flour", SuggestedConfidence.High, """{"qty":500}""");
            session.AddLine(2, "2x Milk", SuggestedConfidence.Low, null);
            sessionId = session.Id;

            await ctx.ImportSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var loaded = await ctx2.ImportSessions
            .Include(s => s.Lines)
            .SingleAsync(s => s.Id == sessionId);

        Assert.Equal(_household, loaded.HouseholdId);
        Assert.Equal(ImportStatus.Parsing, loaded.Status);
        Assert.Equal(2, loaded.Lines.Count);

        var line1 = loaded.Lines.Single(l => l.LineNo == 1);
        Assert.Equal("500g Flour", line1.ReceiptText);
        Assert.Equal(SuggestedConfidence.High, line1.SuggestedConfidence);
        // Postgres JSONB normalizes whitespace: {"qty": 500} is equivalent to {"qty":500}.
        Assert.NotNull(line1.RawParse);
        Assert.Contains("500", line1.RawParse);
        Assert.Equal(LineStatus.Pending, line1.Status);

        var line2 = loaded.Lines.Single(l => l.LineNo == 2);
        Assert.Null(line2.RawParse);
    }

    [Fact(DisplayName = "ImportReceipt binary round-trip preserves bytes")]
    public async Task ImportReceipt_BinaryContent_RoundTrips()
    {
        var content = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 }; // fake JPEG header
        ImportSessionId sessionId;

        await using (var ctx = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            sessionId = session.Id;
            await ctx.ImportSessions.AddAsync(session);

            var receipt = ImportReceipt.Create(
                sessionId, _household, content, "image/jpeg", "abc123sha");
            await ctx.ImportReceipts.AddAsync(receipt);

            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var loaded = await ctx2.ImportReceipts.SingleAsync(r => r.Id == sessionId);

        Assert.Equal(content, loaded.Content);
        Assert.Equal("image/jpeg", loaded.ContentType);
        Assert.Equal(content.LongLength, loaded.ByteSize);
        Assert.Equal("abc123sha", loaded.Sha256);
    }

    [Fact(DisplayName = "Session lifecycle transitions persist correctly")]
    public async Task Session_Lifecycle_Persists_Transitions()
    {
        ImportSessionId sessionId;

        await using (var ctx = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            session.AddLine(1, "Eggs", SuggestedConfidence.High, null);
            sessionId = session.Id;
            await ctx.ImportSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        // Transition to Ready
        await using (var ctx = NewIntakeDb())
        {
            var session = await ctx.ImportSessions.Include(s => s.Lines)
                .SingleAsync(s => s.Id == sessionId);
            session.MarkReady("Whole Foods", DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var loaded = await ctx2.ImportSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(ImportStatus.Ready, loaded.Status);
        Assert.Equal("Whole Foods", loaded.MerchantText);
        Assert.NotNull(loaded.ParsedAt);
    }

    [Fact(DisplayName = "RLS: household B cannot read household A's sessions")]
    public async Task RLS_Household_B_Cannot_Read_Household_A_Sessions()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();

        await using (var ctxA = NewIntakeDbFor(householdA))
        {
            var session = ImportSession.Start(householdA, ImportSourceType.Receipt, _userId, Clock);
            await ctxA.ImportSessions.AddAsync(session);
            await ctxA.SaveChangesAsync();
        }

        await using var ctxB = NewIntakeDbFor(householdB);
        var count = await ctxB.ImportSessions.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact(DisplayName = "FindReceiptAsync returns null when no receipt was added")]
    public async Task FindReceiptAsync_Returns_Null_When_No_Receipt()
    {
        ImportSessionId sessionId;

        await using (var ctx = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            sessionId = session.Id;
            await ctx.ImportSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var repo = new ImportSessionRepository(ctx2);
        var receipt = await repo.FindReceiptAsync(sessionId);

        Assert.Null(receipt);
    }

    [Fact(DisplayName = "ImportLine suggestion fields round-trip through EF")]
    public async Task ImportLine_SuggestionFields_RoundTrip()
    {
        var sugProductId = Guid.NewGuid();
        ImportSessionId sessionId;

        await using (var ctx = NewIntakeDb())
        {
            var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
            session.AddLine(1, "Milk 2L", SuggestedConfidence.High, null,
                suggestedProductId: sugProductId,
                suggestedProductName: "Milk",
                suggestedQuantity: 2m,
                suggestedUnitLabel: "L",
                suggestedPrice: 3.49m);
            sessionId = session.Id;

            await ctx.ImportSessions.AddAsync(session);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var loaded = await ctx2.ImportSessions
            .Include(s => s.Lines)
            .SingleAsync(s => s.Id == sessionId);

        var line = Assert.Single(loaded.Lines);
        Assert.Equal(sugProductId, line.SuggestedProductId);
        Assert.Equal("Milk", line.SuggestedProductName);
        Assert.Equal(2m, line.SuggestedQuantity);
        Assert.Equal("L", line.SuggestedUnitLabel);
        Assert.Equal(3.49m, line.SuggestedPrice);
    }

    [Fact(DisplayName = "ListRecentAsync returns Ready, Committed, Failed — excludes Parsing and Discarded")]
    public async Task ListRecentAsync_Returns_Visible_Statuses_Only()
    {
        var statuses = new[] { ImportStatus.Ready, ImportStatus.Committed, ImportStatus.Failed,
                               ImportStatus.Parsing, ImportStatus.Discarded };

        await using (var ctx = NewIntakeDb())
        {
            foreach (var status in statuses)
            {
                var s = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
                // Advance to target status via the domain transitions
                if (status == ImportStatus.Ready || status == ImportStatus.Committed)
                    s.MarkReady("Shop", DateTimeOffset.UtcNow);
                if (status == ImportStatus.Committed)
                    s.MarkCommitted(DateTimeOffset.UtcNow);
                if (status == ImportStatus.Failed)
                    s.MarkParsingFailed("oops");
                if (status == ImportStatus.Discarded)
                    s.Discard();
                await ctx.ImportSessions.AddAsync(s);
            }
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var repo = new ImportSessionRepository(ctx2);
        var recent = await repo.ListRecentAsync(_household);

        Assert.Equal(3, recent.Count);
        Assert.All(recent, s => Assert.Contains(s.Status,
            new[] { ImportStatus.Ready, ImportStatus.Committed, ImportStatus.Failed }));
    }

    [Fact(DisplayName = "ListRecentAsync orders by CreatedAt descending and respects take")]
    public async Task ListRecentAsync_OrderedNewestFirst_And_TakeRespected()
    {
        var sessionIds = new List<ImportSessionId>();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-5);

        await using (var ctx = NewIntakeDb())
        {
            for (var i = 0; i < 5; i++)
            {
                var s = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
                s.MarkReady($"Shop {i}", baseTime.AddDays(i));
                sessionIds.Add(s.Id);
                await ctx.ImportSessions.AddAsync(s);
            }
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var repo = new ImportSessionRepository(ctx2);
        var recent = await repo.ListRecentAsync(_household, take: 3);

        Assert.Equal(3, recent.Count);
        // Newest three: indices 4, 3, 2
        Assert.True(recent[0].CreatedAt >= recent[1].CreatedAt);
        Assert.True(recent[1].CreatedAt >= recent[2].CreatedAt);
    }

    [Fact(DisplayName = "ListRecentAsync is tenant-scoped: household B cannot see household A sessions")]
    public async Task ListRecentAsync_TenantScoped()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();

        await using (var ctxA = NewIntakeDbFor(householdA))
        {
            var s = ImportSession.Start(householdA, ImportSourceType.Receipt, _userId, Clock);
            s.MarkReady("Shop A", DateTimeOffset.UtcNow);
            await ctxA.ImportSessions.AddAsync(s);
            await ctxA.SaveChangesAsync();
        }

        await using var ctxB = NewIntakeDbFor(householdB);
        var repo = new ImportSessionRepository(ctxB);
        var recent = await repo.ListRecentAsync(householdB);

        Assert.Empty(recent);
    }

    [Fact(DisplayName = "ListInMonthWindowAsync returns sessions created OR committed inside the window")]
    public async Task ListInMonthWindowAsync_Unions_CreatedAt_And_CommittedAt()
    {
        var windowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero);

        ImportSessionId createdInWindow, straddling, createdBefore, createdAfter;

        await using (var ctx = NewIntakeDb())
        {
            // A: created inside the window, never committed → returned via CreatedAt.
            var a = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, new FixedClock(new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)));
            createdInWindow = a.Id;

            // B: created BEFORE the window but committed INSIDE it → returned via CommittedAt.
            var b = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, new FixedClock(new DateTimeOffset(2026, 4, 25, 9, 0, 0, TimeSpan.Zero)));
            b.MarkReady("Store", new DateTimeOffset(2026, 4, 26, 9, 0, 0, TimeSpan.Zero));
            b.MarkCommitted(new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero));
            straddling = b.Id;

            // C: created before, never committed → excluded.
            var c = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, new FixedClock(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero)));
            createdBefore = c.Id;

            // D: created after the window → excluded.
            var d = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, new FixedClock(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero)));
            createdAfter = d.Id;

            await ctx.ImportSessions.AddRangeAsync(a, b, c, d);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var repo = new ImportSessionRepository(ctx2);
        var window = await repo.ListInMonthWindowAsync(_household, windowStart, windowEnd);
        var ids = window.Select(s => s.Id).ToHashSet();

        Assert.Equal(2, window.Count);
        Assert.Contains(createdInWindow, ids);
        Assert.Contains(straddling, ids);
        Assert.DoesNotContain(createdBefore, ids);
        Assert.DoesNotContain(createdAfter, ids);
    }

    [Fact(DisplayName = "ListInMonthWindowAsync is tenant-scoped")]
    public async Task ListInMonthWindowAsync_TenantScoped()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();
        var windowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero);

        await using (var ctxA = NewIntakeDbFor(householdA))
        {
            var s = ImportSession.Start(householdA, ImportSourceType.Receipt, _userId, new FixedClock(new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero)));
            await ctxA.ImportSessions.AddAsync(s);
            await ctxA.SaveChangesAsync();
        }

        await using var ctxB = NewIntakeDbFor(householdB);
        var repo = new ImportSessionRepository(ctxB);
        var window = await repo.ListInMonthWindowAsync(householdB, windowStart, windowEnd);

        Assert.Empty(window);
    }

    [Fact(DisplayName = "ListInMonthWindowAsync accepts a non-UTC-offset window (Npgsql timestamptz safety)")]
    public async Task ListInMonthWindowAsync_NonUtcOffset_Window_DoesNotThrow_And_Filters_Correctly()
    {
        // Regression (plantry-bzyr): the real caller GetMonthlyIntakeStatsQuery builds the window from
        // clock.UtcNow.ToLocalTime(), producing a NON-UTC offset off UTC machines. Npgsql throws
        // "Cannot write DateTimeOffset with Offset=… only offset 0 (UTC) is supported" when such a value
        // is written to a 'timestamp with time zone' parameter, which 500'd the Add-groceries page. The
        // repo must normalize to UTC. The pre-existing window tests only passed UTC (TimeSpan.Zero) bounds,
        // so they never exercised this path.
        var offset = TimeSpan.FromHours(-4);
        var windowStart = new DateTimeOffset(2026, 5, 1, 0, 0, 0, offset);
        var windowEnd = new DateTimeOffset(2026, 5, 31, 23, 59, 59, offset);

        ImportSessionId inWindow;
        await using (var ctx = NewIntakeDb())
        {
            // Created 2026-05-15 12:00 UTC — squarely inside the May window in either frame.
            var s = ImportSession.Start(_household, ImportSourceType.Receipt, _userId,
                new FixedClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)));
            inWindow = s.Id;
            // Created 2026-04-01 — before the window.
            var before = ImportSession.Start(_household, ImportSourceType.Receipt, _userId,
                new FixedClock(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)));
            await ctx.ImportSessions.AddRangeAsync(s, before);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewIntakeDb();
        var repo = new ImportSessionRepository(ctx2);
        // Must not throw despite the non-UTC offset on the window bounds.
        var window = await repo.ListInMonthWindowAsync(_household, windowStart, windowEnd);

        var single = Assert.Single(window);
        Assert.Equal(inWindow, single.Id);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private DbContextOptions<IntakeDbContext> IntakeOptions() =>
        new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(db.ConnectionString).Options;

    private IntakeDbContext NewIntakeDb()
    {
        var ctx = new IntakeDbContext(IntakeOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private IntakeDbContext NewIntakeDbFor(HouseholdId household)
    {
        var ctx = new IntakeDbContext(IntakeOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
