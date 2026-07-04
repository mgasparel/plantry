using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Shopping.Application;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render tests for the Shopping deal badge (P5-9). Proves that a shopping-list item whose product has an
/// active deal renders the "On sale at {store} this week" badge as a tappable anchor to the Deals page, and
/// that an item with no active deal renders no badge — the badge appears/disappears purely from what
/// <see cref="IShoppingDealReader"/> (Pricing's cheapest-active-deal read model) returns, never from stored
/// state (ADR-010 R3/D11). The read-model window filtering itself (deal lapses when today &gt; valid_to) is
/// proven in the Pricing suite (PricingRepositoryTests / PricingQueriesTests, P5-P).
/// </summary>
public sealed class ShoppingDealBadgeTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Deal-bearing reader: the fixture's Milk product has an active deal at "FreshCo".</summary>
    private sealed class FakeDealReaderWithMilkDeal : IShoppingDealReader
    {
        public static readonly Guid DealId = Guid.Parse("55555555-5555-5555-5555-555555555501");

        public Task<IReadOnlyDictionary<Guid, ShoppingActiveDeal>> GetActiveDealsAsync(
            IReadOnlyList<Guid> productIds, DateOnly today, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, ShoppingActiveDeal> result = new Dictionary<Guid, ShoppingActiveDeal>();
            if (productIds.Contains(ShoppingListFixture.MilkProductId))
            {
                result = new Dictionary<Guid, ShoppingActiveDeal>
                {
                    [ShoppingListFixture.MilkProductId] = new(
                        ShoppingListFixture.MilkProductId, DealId, Guid.NewGuid(), "FreshCo"),
                };
            }
            return Task.FromResult(result);
        }
    }

    /// <summary>Base Shopping fragment factory with the deal reader swapped for the Milk-deal reader.</summary>
    private sealed class DealBadgeFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();
                services.RemoveAll<IShoppingDealReader>();
                services.AddSingleton<IShoppingDealReader>(new FakeDealReaderWithMilkDeal());
            });
        }
    }

    [Fact]
    public async Task Product_with_active_deal_renders_tappable_badge()
    {
        using var factory = new DealBadgeFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var badge = doc.QuerySelector("a.sl-deal");
        Assert.NotNull(badge);
        Assert.Contains("On sale at FreshCo this week", badge!.TextContent);
        var href = badge.GetAttribute("href");
        Assert.NotNull(href);
        Assert.StartsWith("/Deals", href);
        Assert.Contains($"#deal-{FakeDealReaderWithMilkDeal.DealId}", href);
    }

    [Fact]
    public async Task Product_with_no_active_deal_renders_no_badge()
    {
        // The shared factory registers an empty deal reader — no product has an active deal.
        using var factory = new ShoppingListFragmentFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        Assert.Empty(doc.QuerySelectorAll("a.sl-deal"));
        Assert.DoesNotContain("On sale", html);
    }
}
