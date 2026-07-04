using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render tests for MealPlan- and Deal-sourced attribution labels on the Shopping board (plantry-jwyb).
/// Proves that the shopping list renders one attribution line per distinct meal-plan slot
/// ("for Mon dinner" / "for Thu dinner"), a resolved deal store label ("on sale at Metro"), and the
/// documented fallbacks ("for your meal plan" / "on sale") — all keyed off the typed
/// <see cref="AttributionKind"/>, not the wording. Uses a dedicated fixture list + resolving readers so the
/// shared snapshot baselines are untouched (mirrors <see cref="ShoppingDealBadgeTests"/>).
/// </summary>
public sealed class ShoppingAttributionRenderTests
{
    private static readonly HtmlParser Parser = new();
    private static readonly IClock Clock = SystemClock.Instance;

    // Fixed slot/deal SourceRefs for the custom list.
    private static readonly Guid SlotMon = Guid.Parse("66666666-6666-6666-6666-666666666601");
    private static readonly Guid SlotThu = Guid.Parse("66666666-6666-6666-6666-666666666602");
    private static readonly Guid ResolvedDealId = Guid.Parse("66666666-6666-6666-6666-666666666603");
    private static readonly Guid UnresolvedDealId = Guid.Parse("66666666-6666-6666-6666-666666666604");
    private static readonly Guid UnresolvedSlot = Guid.Parse("66666666-6666-6666-6666-666666666605");

    /// <summary>
    /// Builds a list with four product-backed items exercising every MealPlan/Deal render path:
    /// Milk = two resolved meal-plan slots; Chicken = one resolved deal store; Flour = unresolved slot
    /// (fallback); Milk-again is not reused — Chicken2 handled via a second deal id on a distinct product.
    /// </summary>
    private static ShoppingList BuildAttributionList()
    {
        var household = HouseholdId.From(ShoppingListFixture.HouseholdAId);
        var list = ShoppingList.Create(household, Clock);

        // MealPlan item across two slots → "for Mon dinner", "for Thu dinner".
        var mealItem = list.AddItem(ShoppingListFixture.MilkProductId, quantity: 2m,
            unitId: ShoppingListFixture.UnitId, note: null,
            source: ItemSource.MealPlan, sourceRef: SlotMon, Clock);
        list.UpsertContribution(mealItem, ItemSource.MealPlan, SlotThu,
            incomingQuantity: 2m, incomingUnitId: ShoppingListFixture.UnitId, Clock);

        // Deal item with a resolved store → "on sale at Metro".
        list.AddItem(ShoppingListFixture.ChickenProductId, quantity: 1m,
            unitId: null, note: null,
            source: ItemSource.Deal, sourceRef: ResolvedDealId, Clock);

        // MealPlan item with an unresolved slot → fallback "for your meal plan".
        list.AddItem(ShoppingListFixture.FlourProductId, quantity: 1m,
            unitId: ShoppingListFixture.UnitId, note: null,
            source: ItemSource.MealPlan, sourceRef: UnresolvedSlot, Clock);

        // Deal item with an unresolved store → fallback "on sale". Free-text so it needs no catalog summary.
        var freeItem = list.AddFreeTextItem("Sale mystery item", quantity: null, unitId: null, note: null, Clock);
        list.UpsertContribution(freeItem, ItemSource.Deal, UnresolvedDealId,
            incomingQuantity: 1m, incomingUnitId: null, Clock);

        return list;
    }

    private sealed class AttributionFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();
                var list = BuildAttributionList();

                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository>(sp =>
                    new FakeShoppingRepository(sp.GetRequiredService<ITenantContext>(), list));

                services.RemoveAll<IShoppingMealPlanReader>();
                services.AddSingleton<IShoppingMealPlanReader>(
                    new FakeShoppingMealPlanReaderForSnapshots(new Dictionary<Guid, ShoppingMealPlanSlot>
                    {
                        [SlotMon] = new(DayOfWeek.Monday, "Dinner"),
                        [SlotThu] = new(DayOfWeek.Thursday, "Dinner"),
                        // UnresolvedSlot intentionally absent → fallback.
                    }));

                services.RemoveAll<IShoppingDealAttributionReader>();
                services.AddSingleton<IShoppingDealAttributionReader>(
                    new FakeShoppingDealAttributionReaderForSnapshots(new Dictionary<Guid, string>
                    {
                        [ResolvedDealId] = "Metro",
                        // UnresolvedDealId intentionally absent → fallback.
                    }));
            });
        }
    }

    private static async Task<string> GetPageAsync()
    {
        using var factory = new AttributionFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "Shopping — MealPlan item renders one attribution line per resolved slot")]
    public async Task MealPlan_item_renders_line_per_slot()
    {
        var html = await GetPageAsync();
        var doc = Parser.ParseDocument(html);
        var texts = doc.QuerySelectorAll(".sl-attribution").Select(a => a.TextContent.Trim()).ToList();

        Assert.Contains(texts, t => t.Contains("for Mon dinner"));
        Assert.Contains(texts, t => t.Contains("for Thu dinner"));

        // MealPlan labels drive no recipe icon (presentation keys off the kind, not the wording).
        var monSpan = doc.QuerySelectorAll(".sl-attribution")
            .First(a => a.TextContent.Contains("for Mon dinner"));
        Assert.DoesNotContain("#i-recipe", monSpan.InnerHtml);
    }

    [Fact(DisplayName = "Shopping — Deal item renders 'on sale at {store}' with the resolved store")]
    public async Task Deal_item_renders_store_label()
    {
        var html = await GetPageAsync();
        var doc = Parser.ParseDocument(html);
        var texts = doc.QuerySelectorAll(".sl-attribution").Select(a => a.TextContent.Trim()).ToList();

        Assert.Contains(texts, t => t.Contains("on sale at Metro"));
    }

    [Fact(DisplayName = "Shopping — unresolved MealPlan slot and Deal store fall back to generic labels")]
    public async Task Unresolved_refs_render_fallback_labels()
    {
        var html = await GetPageAsync();
        var doc = Parser.ParseDocument(html);
        var texts = doc.QuerySelectorAll(".sl-attribution").Select(a => a.TextContent.Trim()).ToList();

        Assert.Contains(texts, t => t.Contains("for your meal plan"));
        Assert.Contains(texts, t => t == "on sale" || t.EndsWith("on sale"));
    }
}
