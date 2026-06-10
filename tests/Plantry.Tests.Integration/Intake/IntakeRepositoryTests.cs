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
