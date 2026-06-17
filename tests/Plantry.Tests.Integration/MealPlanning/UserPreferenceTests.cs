using Microsoft.EntityFrameworkCore;
using Npgsql;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 integration tests for the UserPreference / TagStance tables (acceptance criterion L3).
/// Covers: schema round-trip, EF query filter household isolation, composite FK, RLS via raw SQL,
/// and the stance CHECK constraint rejects invalid values.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UserPreferenceTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _householdA;
    private HouseholdId _householdB;
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _householdA = HouseholdId.New();
        _householdB = HouseholdId.New();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Schema round-trip ──────────────────────────────────────────────────────

    [Fact(DisplayName = "UserPreference can be created, a TagStance added, and read back")]
    public async Task RoundTrip_UserPreference_And_TagStance()
    {
        var tagId = Guid.NewGuid();

        // Write
        await using (var writeDb = NewMealPlanningDb(_householdA))
        {
            var pref = UserPreference.Create(_householdA, UserA, Clock);
            pref.SetStance(tagId, "Required", Clock);
            await writeDb.UserPreferences.AddAsync(pref);
            await writeDb.SaveChangesAsync();
        }

        // Read back (include TagStances via repository)
        await using var readDb = NewMealPlanningDb(_householdA);
        var repo = new UserPreferenceRepository(readDb);
        var loaded = await repo.FindByUserIdAsync(UserA);

        Assert.NotNull(loaded);
        Assert.Equal(_householdA, loaded.HouseholdId);
        var stance = Assert.Single(loaded.TagStances);
        Assert.Equal(tagId, stance.TagId);
        Assert.Equal("Required", stance.Stance);
    }

    [Fact(DisplayName = "UNIQUE (user_preference_id, tag_id) — duplicate tag in same profile throws")]
    public async Task UniqueConstraint_UserPref_Tag_ThrowsOnDuplicate()
    {
        var tagId = Guid.NewGuid();

        await using var writeDb = NewMealPlanningDb(_householdA);
        var pref = UserPreference.Create(_householdA, UserA, Clock);
        pref.SetStance(tagId, "Preferred", Clock);
        await writeDb.UserPreferences.AddAsync(pref);
        await writeDb.SaveChangesAsync();

        // Try inserting a second TagStance for the same tag via raw SQL to bypass the domain
        // (the domain's SetStance prevents this, but we need to verify the DB constraint directly).
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO meal_planning.tag_stance (tag_stance_id, household_id, user_preference_id, tag_id, stance)
            VALUES (gen_random_uuid(), '{_householdA.Value}', '{pref.Id.Value}', '{tagId}', 'Disliked')
            """;

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact(DisplayName = "stance CHECK constraint rejects invalid values")]
    public async Task Check_StanceValue_RejectsInvalid()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // First insert a valid user_preference row so we have a valid user_preference_id
        var prefId = Guid.NewGuid();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO meal_planning.user_preference
                    (user_preference_id, household_id, user_id, created_at, updated_at)
                VALUES ('{prefId}', '{_householdA.Value}', gen_random_uuid(), now(), now())
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO meal_planning.tag_stance (tag_stance_id, household_id, user_preference_id, tag_id, stance)
            VALUES (gen_random_uuid(), '{_householdA.Value}', '{prefId}', gen_random_uuid(), 'BadValue')
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "EF query filter: household A cannot see household B's preferences")]
    public async Task EfFilter_HouseholdA_Cannot_Read_HouseholdB_Preferences()
    {
        var userB = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        // Seed pref for household B
        await using (var seedDb = NewMealPlanningDb(_householdB))
        {
            var pref = UserPreference.Create(_householdB, userB, Clock);
            pref.SetStance(tagId, "Preferred", Clock);
            await seedDb.UserPreferences.AddAsync(pref);
            await seedDb.SaveChangesAsync();
        }

        // Read as household A — should see nothing
        await using var readAsA = NewMealPlanningDb(_householdA);
        var found = await readAsA.UserPreferences.ToListAsync();

        Assert.Empty(found);
    }

    [Fact(DisplayName = "RLS backstop: raw SQL with wrong household returns no user_preference rows")]
    public async Task RlsPolicy_RawSql_WrongHousehold_ReturnsNoRows()
    {
        var userB = Guid.NewGuid();

        // Seed for household B
        await using (var seedDb = NewMealPlanningDb(_householdB))
        {
            var pref = UserPreference.Create(_householdB, userB, Clock);
            await seedDb.UserPreferences.AddAsync(pref);
            await seedDb.SaveChangesAsync();
        }

        // Query as app_user with household A's id — RLS should hide B's rows
        await using var conn = new NpgsqlConnection(db.AppUserConnectionString);
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.household_id = '{_householdA.Value}'";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var select = conn.CreateCommand();
        select.CommandText = "SELECT user_preference_id FROM meal_planning.user_preference";
        await using var reader = await select.ExecuteReaderAsync();

        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));

        // B's rows must not appear when queried as A
        Assert.DoesNotContain(ids, _ => true); // empty because A has no rows
    }

    [Fact(DisplayName = "Composite FK: tag_stance.household_id must match user_preference.household_id")]
    public async Task CompositeFk_TagStance_HouseholdMustMatch()
    {
        // Insert a valid user_preference for household A
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var prefId = Guid.NewGuid();
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO meal_planning.user_preference
                    (user_preference_id, household_id, user_id, created_at, updated_at)
                VALUES ('{prefId}', '{_householdA.Value}', gen_random_uuid(), now(), now())
                """;
            await insert.ExecuteNonQueryAsync();
        }

        // Attempt to insert a tag_stance with household B's id but pointing to household A's pref —
        // the composite FK (household_id, user_preference_id) references user_preference should reject this.
        await using var badInsert = conn.CreateCommand();
        badInsert.CommandText = $"""
            INSERT INTO meal_planning.tag_stance
                (tag_stance_id, household_id, user_preference_id, tag_id, stance)
            VALUES (gen_random_uuid(), '{_householdB.Value}', '{prefId}', gen_random_uuid(), 'Preferred')
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => badInsert.ExecuteNonQueryAsync());
        // 23503 = foreign_key_violation
        Assert.Equal("23503", ex.SqlState);
    }

    private DbContextOptions<MealPlanningDbContext> MealPlanningOptions() =>
        new DbContextOptionsBuilder<MealPlanningDbContext>().UseNpgsql(db.ConnectionString).Options;

    private MealPlanningDbContext NewMealPlanningDb(HouseholdId household)
    {
        var ctx = new MealPlanningDbContext(MealPlanningOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
