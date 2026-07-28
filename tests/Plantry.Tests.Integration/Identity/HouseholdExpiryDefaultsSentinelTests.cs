using Microsoft.EntityFrameworkCore;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Identity;

/// <summary>
/// L3 proof for the <c>HasSentinel(-1)</c> fix (plantry-hw39, absorbing plantry-bjal) on
/// <see cref="Household.DefaultDueDaysAfterFreezing"/>/<see cref="Household.DefaultDueDaysAfterThawing"/>.
///
/// Before the fix, <c>HasDefaultValue(90)</c>/<c>(3)</c> made EF treat the CLR default <c>0</c> as
/// "not set, use the column default", so a <see cref="Household"/> INSERTed with both fields explicitly
/// set to <c>0</c> silently landed in the row as <c>90</c>/<c>3</c> — VERIFIED BY EXECUTION against real
/// Postgres per the original plantry-bjal report. The UPDATE path (through
/// <see cref="HouseholdExpiryDefaultsService"/>) was never affected, since UPDATE always sends the
/// explicit value; only a fresh INSERT with a deliberate <c>0</c>/<c>0</c> hit the bug.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class HouseholdExpiryDefaultsSentinelTests(PostgresFixture db) : IAsyncLifetime
{
    public async Task InitializeAsync() => await db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "A household INSERTed with DefaultDueDaysAfterFreezing/Thawing = 0/0 persists as 0/0, not the 90/3 column default")]
    public async Task Household_Inserted_With_Zero_Zero_Persists_As_Zero_Zero()
    {
        HouseholdId householdId;
        await using (var writeDb = new PlantryIdentityDbContext(Options()))
        {
            var household = Household.Create("Household hw39-sentinel", SystemClock.Instance);
            household.SetDefaultDueDaysAfterFreezing(0);
            household.SetDefaultDueDaysAfterThawing(0);
            await writeDb.Households.AddAsync(household);
            await writeDb.SaveChangesAsync();
            householdId = household.Id;
        }

        await using var readDb = new PlantryIdentityDbContext(Options());
        readDb.SetHouseholdId(householdId.Value);
        var persisted = await readDb.Households.SingleAsync(h => h.Id == householdId);

        Assert.Equal(0, persisted.DefaultDueDaysAfterFreezing);
        Assert.Equal(0, persisted.DefaultDueDaysAfterThawing);
    }

    [Fact(DisplayName = "A household INSERTed with no explicit call still gets the 90/3 column default (sentinel change doesn't disturb the backfill/new-row default)")]
    public async Task Household_Inserted_Without_Explicit_Call_Still_Defaults_NinetyThree()
    {
        HouseholdId householdId;
        await using (var writeDb = new PlantryIdentityDbContext(Options()))
        {
            var household = Household.Create("Household hw39-default", SystemClock.Instance);
            await writeDb.Households.AddAsync(household);
            await writeDb.SaveChangesAsync();
            householdId = household.Id;
        }

        await using var readDb = new PlantryIdentityDbContext(Options());
        readDb.SetHouseholdId(householdId.Value);
        var persisted = await readDb.Households.SingleAsync(h => h.Id == householdId);

        Assert.Equal(90, persisted.DefaultDueDaysAfterFreezing);
        Assert.Equal(3, persisted.DefaultDueDaysAfterThawing);
    }

    private DbContextOptions<PlantryIdentityDbContext> Options() =>
        new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(db.ConnectionString).Options;
}
