using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 integration test for the Recipes reference-data seeder (DM-9): registering a household seeds
/// exactly the ten default tags, with the right names and categories, scoped to that household.
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

    [Fact(DisplayName = "Seeding a household creates exactly the 10 default tags with plant-protein vocabulary")]
    public async Task Seeding_Creates_The_Ten_Default_Tags()
    {
        await using var read = NewRecipesDb(_household);
        var tags = await read.Tags.ToListAsync();

        Assert.Equal(10, tags.Count);
        Assert.All(tags, t => Assert.Equal(_household, t.HouseholdId));

        var byCategory = tags
            .GroupBy(t => t.Category!.Value)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).OrderBy(n => n).ToList());

        Assert.Equal(
            new[] { "Dairy-Free", "Gluten-Free", "Vegan", "Vegetarian" },
            byCategory[TagCategory.Diet]);
        Assert.Equal(
            new[] { "Fish", "Legumes", "Meat", "Poultry", "Tofu" },
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

    [Fact(DisplayName = "Plant-protein rollout upgrades an original eight-tag household and is repeatable")]
    public async Task Plant_Protein_Rollout_Adds_Only_Missing_Tags_And_Is_Idempotent()
    {
        await db.ResetAsync();
        var household = Household.Create("Original household", SystemClock.Instance);

        await using (var identity = new PlantryIdentityDbContext(IdentityOptions()))
        {
            await identity.Households.AddAsync(household);
            await identity.SaveChangesAsync();
        }

        List<Tag> originalTags =
        [
            Tag.Create(household.Id, "Vegetarian", TagCategory.Diet, SystemClock.Instance),
            Tag.Create(household.Id, "Vegan", TagCategory.Diet, SystemClock.Instance),
            Tag.Create(household.Id, "Dairy-Free", TagCategory.Diet, SystemClock.Instance),
            Tag.Create(household.Id, "Gluten-Free", TagCategory.Diet, SystemClock.Instance),
            Tag.Create(household.Id, "Meat", TagCategory.Protein, SystemClock.Instance),
            Tag.Create(household.Id, "Poultry", TagCategory.Protein, SystemClock.Instance),
            Tag.Create(household.Id, "Fish", TagCategory.Protein, SystemClock.Instance),
            Tag.Create(household.Id, "Spicy", TagCategory.Flavor, SystemClock.Instance),
        ];
        await using (var recipes = NewRecipesDb(household.Id))
        {
            await recipes.Tags.AddRangeAsync(originalTags);
            await recipes.SaveChangesAsync();
        }

        var originalByName = originalTags.ToDictionary(tag => tag.Name, tag => (tag.Id, tag.Category));
        await using var services = BuildRolloutServices();
        var rollout = services.GetRequiredService<RecipesReferenceDataRollout>();

        await rollout.RunAsync();
        await rollout.RunAsync();

        await using var read = NewRecipesDb(household.Id);
        var tags = await read.Tags.OrderBy(tag => tag.Name).ToListAsync();

        Assert.Equal(10, tags.Count);
        Assert.Equal(1, tags.Count(tag => tag.Name == "Tofu" && tag.Category == TagCategory.Protein));
        Assert.Equal(1, tags.Count(tag => tag.Name == "Legumes" && tag.Category == TagCategory.Protein));
        Assert.All(originalByName, original =>
        {
            var persisted = Assert.Single(tags, tag => tag.Name == original.Key);
            Assert.Equal(original.Value.Id, persisted.Id);
            Assert.Equal(original.Value.Category, persisted.Category);
        });
    }

    private DbContextOptions<RecipesDbContext> RecipesOptions() =>
        new DbContextOptionsBuilder<RecipesDbContext>().UseNpgsql(db.ConnectionString).Options;

    private DbContextOptions<PlantryIdentityDbContext> IdentityOptions() =>
        new DbContextOptionsBuilder<PlantryIdentityDbContext>().UseNpgsql(db.ConnectionString).Options;

    private RecipesDbContext NewRecipesDb(HouseholdId household)
    {
        var ctx = new RecipesDbContext(RecipesOptions());
        ctx.SetHouseholdId(household.Value);
        return ctx;
    }

    private ServiceProvider BuildRolloutServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IClock>(_ => SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<HouseholdRlsConnectionInterceptor>();
        services.AddDbContext<PlantryIdentityDbContext>((sp, options) =>
            options.UseNpgsql(db.AppUserConnectionString)
                .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
        services.AddScoped<IHouseholdRepository, HouseholdRepository>();
        services.AddDbContext<RecipesDbContext>((sp, options) =>
            options.UseNpgsql(db.AppUserConnectionString)
                .AddInterceptors(sp.GetRequiredService<HouseholdRlsConnectionInterceptor>()));
        services.AddScoped<RecipesReferenceDataSeeder>();
        services.AddSingleton<RecipesReferenceDataRollout>();
        return services.BuildServiceProvider();
    }
}
