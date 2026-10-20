using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Web.MealPlanning;

public sealed class MealCardRecipeLinkTests
{
    [Fact(DisplayName = "Cooked meal cards retain their resolvable recipe links")]
    public async Task CookedMeals_KeepRecipeLinks()
    {
        await using var factory = new RecipeLinkFactory(markDishesCooked: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RecipeLinkFixture.HouseholdId.ToString());

        var response = await client.GetAsync("/MealPlan");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("meal-card  cooked", html);
        Assert.Equal(4, Count(html, "class=\"md-name md-recipe-link\""));
    }

    [Theory(DisplayName = "Meal card links only resolved recipe dishes on page and grid fragment")]
    [InlineData("/MealPlan")]
    [InlineData("/MealPlan?handler=Grid")]
    public async Task MixedMeal_OnlyResolvedRecipesAreLinks(string path)
    {
        await using var factory = new RecipeLinkFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RecipeLinkFixture.HouseholdId.ToString());

        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(2, Count(html, $"<a class=\"md-name md-recipe-link\" href=\"/Recipes/{RecipeLinkFixture.RecipeAId}\">Long Recipe A</a>"));
        Assert.Equal(2, Count(html, $"<a class=\"md-name md-recipe-link\" href=\"/Recipes/{RecipeLinkFixture.RecipeBId}\">Recipe B</a>"));
        Assert.Equal(2, Count(html, "<span class=\"md-name\">Oat milk</span>"));
        Assert.Equal(2, Count(html, "<span class=\"md-name\">Unknown recipe</span>"));
        Assert.Equal(4, Count(html, "class=\"md-name md-recipe-link\""));
        Assert.DoesNotContain($"href=\"/Recipes/{RecipeLinkFixture.ProductId}\"", html);
        Assert.DoesNotContain($"href=\"/Recipes/{RecipeLinkFixture.MissingRecipeId}\"", html);
    }

    private static int Count(string source, string value) => source.Split(value).Length - 1;
}

public sealed class RecipeLinkFactory : MealPlanFragmentFactory
{
    private readonly bool _markDishesCooked;

    public RecipeLinkFactory(bool markDishesCooked = false) => _markDishesCooked = markDishesCooked;

    protected override IMealPlanRepository MealPlanRepo => new RecipeLinkMealPlanRepo();
    protected override IRecipeReadModel RecipeReadModel => new FakeRecipeReader(
    [
        new RecipeReadModel(RecipeLinkFixture.RecipeAId, "Long Recipe A", [], 2),
        new RecipeReadModel(RecipeLinkFixture.RecipeBId, "Recipe B", [], 2),
    ]);
    protected override IMealPlanCatalogProductReader CatalogProductReader => new FakeProductReader(
        [new MealPlanProductReadModel(RecipeLinkFixture.ProductId, "Oat milk", "ea", RecipeLinkFixture.UnitId, "count")]);
    protected override IMealPlanWeekReadModel WeekReadModel => new RecipeLinkWeekReadModel();
    protected override IMealPlanCookStatusReader CookStatusReader => _markDishesCooked
        ? new AllDishesCookedReader()
        : base.CookStatusReader;
}

internal sealed class AllDishesCookedReader : IMealPlanCookStatusReader
{
    public Task<IReadOnlyDictionary<Guid, DishCookStatus>> GetStatusesAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DishCookStatus> statuses = plannedDishIds.ToDictionary(
            id => id,
            _ => new DishCookStatus(MealPlanningTestClock.Instant));
        return Task.FromResult(statuses);
    }
}

internal static class RecipeLinkFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("44444444-0000-0000-0000-000000000004");
    public static readonly Guid RecipeAId = Guid.Parse("55555555-0000-0000-0000-000000000005");
    public static readonly Guid RecipeBId = Guid.Parse("66666666-0000-0000-0000-000000000006");
    public static readonly Guid MissingRecipeId = Guid.Parse("77777777-0000-0000-0000-000000000007");
    public static readonly Guid ProductId = Guid.Parse("88888888-0000-0000-0000-000000000008");
    public static readonly Guid UnitId = Guid.Parse("99999999-0000-0000-0000-000000000009");
    public static readonly MealSlotId SlotId = WeekGridFixture.SharedConfig.Slots.Where(s => s.IsActive).OrderBy(s => s.Ordinal).First().Id;
}

internal sealed class RecipeLinkMealPlanRepo : IMealPlanRepository
{
    private readonly MealPlan _plan = BuildPlan();

    private static MealPlan BuildPlan()
    {
        var hhId = HouseholdId.From(RecipeLinkFixture.HouseholdId);
        var monday = MealPlan.NormalizeToMonday(DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime));
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        var plan = MealPlan.Start(hhId, monday, clock);
        plan.AssignMeal(monday, RecipeLinkFixture.SlotId,
            MixedDishes(), null, "manual", Guid.Empty, clock);
        plan.AssignMeal(monday.AddDays(2), RecipeLinkFixture.SlotId,
            MixedDishes(), null, "manual", Guid.Empty, clock);
        return plan;
    }

    private static DishSpec[] MixedDishes() =>
    [
        DishSpec.ForRecipe(RecipeLinkFixture.RecipeAId, 2),
        DishSpec.ForProduct(RecipeLinkFixture.ProductId, 1m, RecipeLinkFixture.UnitId),
        DishSpec.ForRecipe(RecipeLinkFixture.RecipeBId, 2),
        DishSpec.ForRecipe(RecipeLinkFixture.MissingRecipeId, 1),
    ];

    public Task<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>> FindSlotLabelsAsync(IReadOnlyList<Guid> plannedMealIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PlannedMealSlotInfo>>(new Dictionary<Guid, PlannedMealSlotInfo>());
    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(_plan);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(_plan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class RecipeLinkWeekReadModel : IMealPlanWeekReadModel
{
    private readonly WeekBag _bag = new(
        new Dictionary<Guid, RecipeFact>
        {
            [RecipeLinkFixture.RecipeAId] = new(RecipeLinkFixture.RecipeAId, "Long Recipe A", 2),
            [RecipeLinkFixture.RecipeBId] = new(RecipeLinkFixture.RecipeBId, "Recipe B", 2),
        },
        new Dictionary<Guid, IReadOnlyList<IngredientFact>>(),
        new Dictionary<Guid, ProductFact>(),
        new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
        new Dictionary<Guid, UnitFact> { [RecipeLinkFixture.UnitId] = new(RecipeLinkFixture.UnitId, "ea", "Each", "count", null, true) },
        new Dictionary<Guid, StockFact>(),
        new Dictionary<Guid, PriceFact>());

    public Task<WeekBag> LoadAsync(IReadOnlyList<Guid> recipeIds, IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult(_bag);
}
