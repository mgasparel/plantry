using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.MealPlanning;
using Plantry.Tests.Web.Today;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 regression coverage for plantry-r2yf AC7: the "unresolvable product" placeholder text
/// (<see cref="DishDisplayPlaceholders.UnknownProductName"/> / <see cref="DishDisplayPlaceholders.UnresolvedUnitCode"/>)
/// used to be maintained as separate string literals in Today's and MealPlan's page models, with
/// only a code comment tying them together — nothing failed if one surface's wording drifted from
/// the other's. Both now consume the single shared static; this test proves the two independent
/// HTTP surfaces (Today's planned-meals band, MealPlan's week-grid cell) render byte-identical
/// placeholder text for the same "product id absent from the catalog" case.
/// </summary>
public sealed class CrossSurfaceProductPlaceholderTests
{
    [Fact(DisplayName = "GET /Today and GET /MealPlan render the exact same placeholder for an unresolvable product (AC7)")]
    public async Task BothSurfaces_RenderSamePlaceholder_ForUnresolvableProduct()
    {
        // ── Today ────────────────────────────────────────────────────────────
        await using var todayFactory =
            new TodayRecipeBatchingFactory(TodayRecipeBatchingFixture.BuildProductOnlyPlan());
        var todayClient = todayFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        todayClient.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, TodayRecipeBatchingFixture.HouseholdId.ToString());

        var todayResponse = await todayClient.GetAsync("/Today");
        todayResponse.EnsureSuccessStatusCode();
        var todayHtml = await todayResponse.Content.ReadAsStringAsync();

        // ── MealPlan ─────────────────────────────────────────────────────────
        await using var mealPlanFactory = new MealPlanUnresolvedProductFactory();
        var mealPlanClient = mealPlanFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        mealPlanClient.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, UnresolvedProductFixture.HouseholdId.ToString());

        var mealPlanResponse = await mealPlanClient.GetAsync("/MealPlan");
        mealPlanResponse.EnsureSuccessStatusCode();
        var mealPlanHtml = await mealPlanResponse.Content.ReadAsStringAsync();

        // Both surfaces resolve their product dish (FakeTodayNullCatalogProductReader / the default
        // MealPlanFragmentFactory catalog reader with the id deliberately absent from its product
        // list) via a batched call whose result omits the id — driving each page model's own
        // GetValueOrDefault(id, DishDisplayPlaceholders.X) fallback, not a test-double literal.
        Assert.Contains(DishDisplayPlaceholders.UnknownProductName, todayHtml);
        Assert.Contains(DishDisplayPlaceholders.UnknownProductName, mealPlanHtml);

        // Today renders "2 ?" (servings + unresolved unit, plantry-nlg4 formatting); MealPlan's
        // grid cell renders the same unresolved-unit placeholder for its own product dish. Partial
        // match on the "act-srv\">…" fragment (not the full opening tag) mirrors the established
        // pattern in MealPlanProductResolutionBatchingTests.cs's own act-srv assertions.
        Assert.Contains($"2 {DishDisplayPlaceholders.UnresolvedUnitCode}", todayHtml);
        Assert.Contains($"act-srv\">2 {DishDisplayPlaceholders.UnresolvedUnitCode}</span>", mealPlanHtml);
    }
}

// ── MealPlan-side fixture: a product dish whose id is absent from the catalog reader ──────────

internal static class UnresolvedProductFixture
{
    public static readonly Guid HouseholdId = Guid.Parse("99999999-0000-0000-0000-000000000009");
    private static readonly HouseholdId HhId = SharedKernel.HouseholdId.From(HouseholdId);

    public static readonly MealSlotConfig SlotConfig =
        MealSlotConfig.CreateWithDefaults(HhId, new FixedClock(MealPlanningTestClock.Instant));

    private static readonly List<MealSlot> OrderedSlots = [.. SlotConfig.Slots.OrderBy(s => s.Ordinal)];
    public static readonly MealSlotId BreakfastSlotId = OrderedSlots[0].Id;

    /// <summary>Deliberately never registered with any catalog product reader in this suite.</summary>
    public static readonly Guid MysteryProductId = Guid.CreateVersion7();
}

internal sealed class UnresolvedProductMealPlanRepo : IMealPlanRepository
{
    private static readonly IClock Clock = new FixedClock(MealPlanningTestClock.Instant);

    public MealPlan ThisWeekPlan { get; }
    public DateOnly ThisWeekMonday { get; }

    public UnresolvedProductMealPlanRepo()
    {
        var hhId = SharedKernel.HouseholdId.From(UnresolvedProductFixture.HouseholdId);
        var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
        ThisWeekMonday = MealPlan.NormalizeToMonday(today);
        ThisWeekPlan = MealPlan.Start(hhId, ThisWeekMonday, Clock);

        ThisWeekPlan.AssignMeal(ThisWeekMonday, UnresolvedProductFixture.BreakfastSlotId,
            [new DishSpec(DishKind.Product, UnresolvedProductFixture.MysteryProductId, 2)],
            null, "manual", Guid.Empty, Clock);
    }

    public Task<MealPlan?> FindByWeekAsync(HouseholdId householdId, DateOnly weekStart, CancellationToken ct = default)
        => Task.FromResult<MealPlan?>(weekStart == ThisWeekMonday ? ThisWeekPlan : null);
    public Task<MealPlan> FindOrCreateAsync(HouseholdId householdId, DateOnly weekStart, IClock clock, CancellationToken ct = default)
        => Task.FromResult(ThisWeekPlan);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// MealPlan week-grid factory whose plan has a single product dish (Breakfast, Monday) with an id
/// deliberately absent from the default <c>CatalogProductReader</c>'s (<c>FakeProductReader</c>
/// over <c>WeekGridFixture.Products</c>) product list — so both <c>ResolveNamesAsync</c> (which
/// filters to known ids) and <c>ResolveDefaultUnitCodesAsync</c> (the interface's default empty-dict
/// implementation, unoverridden by <c>FakeProductReader</c>) omit it, exercising Index.cshtml.cs's
/// own <c>DishDisplayPlaceholders</c> fallback rather than a test double's.
/// </summary>
internal sealed class MealPlanUnresolvedProductFactory : MealPlanFragmentFactory
{
    public UnresolvedProductMealPlanRepo Repo { get; } = new();

    protected override string FakeUserId => "00000000-0000-0000-0000-0000000000fe";
    protected override IMealPlanRepository MealPlanRepo => Repo;
    protected override IMealSlotConfigRepository SlotConfigRepo => new FakeSlotRepo(UnresolvedProductFixture.SlotConfig);
    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([]);
    protected override IMealPlanCookStatusReader CookStatusReader =>
        new FixedCookStatusReader(new Dictionary<Guid, DishCookStatus>());
}
