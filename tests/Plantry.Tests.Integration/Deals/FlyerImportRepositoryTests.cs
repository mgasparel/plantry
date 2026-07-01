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
/// store_id, flyer_external_id)</c> — holds under a real re-pull, and the dedup lookup is RLS-scoped so
/// two households can pull the same flyer id without collision.
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

    [Fact(DisplayName = "Dedup: re-inserting the same (household, store, flyer_external_id) violates the unique index (DD5)")]
    public async Task DedupUnique_Holds_On_Repull()
    {
        var household = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(household))
        {
            await new FlyerImportRepository(ctx).AddAsync(Import(household, store, "flyer-1"));
            await ctx.SaveChangesAsync();
        }

        // FindByDedupKey resolves the first pull (the DD5 lookup the worker uses to branch no-op/refresh).
        await using (var ctx = NewContext(household))
        {
            var found = await new FlyerImportRepository(ctx).FindByDedupKeyAsync(store, "flyer-1");
            Assert.NotNull(found);
        }

        // A naive second insert of the same key is rejected by the DB unique index.
        await using (var ctx = NewContext(household))
        {
            await new FlyerImportRepository(ctx).AddAsync(Import(household, store, "flyer-1"));
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact(DisplayName = "Dedup is household-scoped: two households may hold the same (store, flyer_external_id)")]
    public async Task DedupUnique_Is_Household_Scoped()
    {
        var householdA = HouseholdId.New();
        var householdB = HouseholdId.New();
        var store = Guid.NewGuid();

        await using (var ctx = NewContext(householdA))
        {
            await new FlyerImportRepository(ctx).AddAsync(Import(householdA, store, "flyer-shared"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext(householdB))
        {
            await new FlyerImportRepository(ctx).AddAsync(Import(householdB, store, "flyer-shared"));
            await ctx.SaveChangesAsync(); // no collision — different household

            // B's dedup lookup resolves only B's row; A's is invisible.
            var found = await new FlyerImportRepository(ctx).FindByDedupKeyAsync(store, "flyer-shared");
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
