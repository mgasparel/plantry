using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration test for the Recipes reference-data seeder (DM-9): registering a household seeds
/// exactly the eight default tags, with the right names and categories, scoped to that household.
/// The fourth <see cref="TagCategory"/> value (Cuisine) ships with no seeded default — only user-minted
/// inline (recipes-domain-model.md §5) — which the exact-set assertion below guards.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RecipesReferenceDataTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private HouseholdId _otherHousehold;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();
        _otherHousehold = HouseholdId.New();

        // Seed only _household — _otherHousehold is left untouched to prove household scoping.
        await using var seedDb = NewRecipesDb(_household);
        var seeder = new RecipesReferenceDataSeeder(seedDb, SystemClock.Instance);
        await seeder.SeedAsync(_household);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Seeding a household creates exactly the 8 default tags with correct names and categories")]
    public async Task Seeding_Creates_The_Eight_Default_Tags()
    {
        await using var read = NewRecipesDb(_household);
        var tags = await read.Tags.ToListAsync();

        Assert.Equal(8, tags.Count);
        Assert.All(tags, t => Assert.Equal(_household, t.HouseholdId));

        var byCategory = tags
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).OrderBy(n => n).ToList());

        Assert.Equal(
            new[] { "Dairy-Free", "Gluten-Free", "Vegan", "Vegetarian" },
            byCategory[TagCategory.Diet]);
        Assert.Equal(
            new[] { "Fish", "Meat", "Poultry" },
            byCategory[TagCategory.Protein]);
        Assert.Equal(
            new[] { "Spicy" },
            byCategory[TagCategory.Flavor]);

        // Cuisine ships with no seeded default.
        Assert.DoesNotContain(tags, t => t.Category == TagCategory.Cuisine);
    }

    [Fact(DisplayName = "Seeded tags are household-scoped: an unseeded household sees zero tags")]
    public async Task Tags_Are_Household_Scoped()
    {
        await using var read = NewRecipesDb(_otherHousehold);
        var tags = await read.Tags.ToListAsync();

        Assert.Empty(tags);
    }

    private DbContextOptions<RecipesDbContext> RecipesOptions() =>
        new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;

    private RecipesDbContext NewRecipesDb(HouseholdId household)
    {
        var ctx = new RecipesDbContext(RecipesOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }
}
