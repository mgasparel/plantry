using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Deals;

/// <summary>
/// L3 tests for <see cref="FlyerImportRepository"/> (P5-6): the DD5 dedup unique — <c>(household_id,
/// store_id, flyer_external_id)</c> WHERE <c>status='parsed'</c> — holds for Parsed envelopes under a real
/// re-pull yet lets Failed attempts accumulate as retained audit rows (plantry-0l05), and the dedup lookup is
/// RLS-scoped and Parsed-only so a Failed-only history retries as a fresh pull.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FlyerImportRepositoryTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;

    public async Task InitializeAsync() => await db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)).Value;

    private static FlyerImport Import(HouseholdId household, Guid store, string externalId) =>
        FlyerImport.Start(household, store, externalId, contentHash: [1, 2, 3], Window(), "{\"raw\":1}", Clock);

    private static FlyerImport ParsedImport(HouseholdId household, Guid store, string externalId)
    {
        var import = Import(household, store, externalId);
        import.MarkParsed(pendingCount: 0, Clock);
        return import;
    }

    private static FlyerImport FailedImport(HouseholdId household, Guid store, string externalId)
    {
        var import = Import(household, store, externalId);
        import.MarkFailed("materialize fault", Clock);
        return import;
    }

    [Fact(DisplayName = "Dedup: re-inserting the same (household, store, flyer_external_id) as a second Parsed row violates the partial unique index (DD5)")]
    public async Task DedupUnique_Holds_On_Parsed_Repull()
    {
        var household = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(household))
        {
            await new FlyerImportRepository(ctx).AddAsync(ParsedImport(household, store, "flyer-1"));
            await ctx.SaveChangesAsync();
        }

        // FindParsedByDedupKey resolves the first Parsed pull (the DD5 lookup the worker uses to branch no-op/refresh).
        await using (var ctx = NewContext(household))
        {
            var found = await new FlyerImportRepository(ctx).FindParsedByDedupKeyAsync(store, "flyer-1");
            Assert.NotNull(found);
        }

        // A naive second Parsed insert of the same key is rejected by the partial unique index.
        await using (var ctx = NewContext(household))
        {
            await new FlyerImportRepository(ctx).AddAsync(ParsedImport(household, store, "flyer-1"));
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact(DisplayName = "Failed attempts are NOT dedup-constrained: many Failed rows may share one (household, store, flyer_external_id) (plantry-0l05)")]
    public async Task Failed_Rows_Accumulate_Without_Collision()
    {
        var household = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(household))
        {
            var repo = new FlyerImportRepository(ctx);
            await repo.AddAsync(FailedImport(household, store, "flyer-1"));
            await repo.AddAsync(FailedImport(household, store, "flyer-1"));
            await ctx.SaveChangesAsync(); // both persist — the partial index excludes Failed rows
        }

        await using (var ctx = NewContext(household))
        {
            var failed = await ctx.FlyerImports
                .Where(f => f.StoreId == store && f.Status == PullStatus.Failed)
                .ToListAsync();
            Assert.Equal(2, failed.Count);
        }
    }

    [Fact(DisplayName = "Parsed lookup: a Failed-only history returns null so the flyer retries as a fresh pull (plantry-0l05)")]
    public async Task FindParsed_Returns_Null_For_Failed_Only_History()
    {
        var household = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(household))
        {
            await new FlyerImportRepository(ctx).AddAsync(FailedImport(household, store, "flyer-1"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(household))
        {
            var found = await new FlyerImportRepository(ctx).FindParsedByDedupKeyAsync(store, "flyer-1");
            Assert.Null(found); // poison-pill gone: the Failed row no longer occupies the dedup key
        }
    }

    [Fact(DisplayName = "Parsed lookup: finds the Parsed row among a mixed Failed+Parsed history (plantry-0l05)")]
    public async Task FindParsed_Finds_Parsed_Among_Mixed_History()
    {
        var household = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(household))
        {
            var repo = new FlyerImportRepository(ctx);
            await repo.AddAsync(FailedImport(household, store, "flyer-1"));
            await repo.AddAsync(FailedImport(household, store, "flyer-1"));
            var parsed = ParsedImport(household, store, "flyer-1");
            await repo.AddAsync(parsed);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(household))
        {
            var found = await new FlyerImportRepository(ctx).FindParsedByDedupKeyAsync(store, "flyer-1");
            Assert.NotNull(found);
            Assert.Equal(PullStatus.Parsed, found!.Status);
        }
    }

    [Fact(DisplayName = "Dedup is household-scoped: two households may hold the same Parsed (store, flyer_external_id)")]
    public async Task DedupUnique_Is_Household_Scoped()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(householdA))
        {
            await new FlyerImportRepository(ctx).AddAsync(ParsedImport(householdA, store, "flyer-shared"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(householdB))
        {
            await new FlyerImportRepository(ctx).AddAsync(ParsedImport(householdB, store, "flyer-shared"));
            await ctx.SaveChangesAsync(); // no collision — different household

            // B's dedup lookup resolves only B's row; A's is invisible.
            var found = await new FlyerImportRepository(ctx).FindParsedByDedupKeyAsync(store, "flyer-shared");
            Assert.Equal(householdB, found!.HouseholdId);
        }
    }

    private DealsDbContext NewContext(HouseholdId household)
    {
        var options = new DbContextOptionsBuilder<DealsDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;
        var ctx = new DealsDbContext(options);
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
