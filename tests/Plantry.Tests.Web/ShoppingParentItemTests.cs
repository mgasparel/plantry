using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render tests for a parent product as a shopping-list item (plantry-oh27.4 — "Bubly" is addable
/// from the picker/suggestions strip and renders a correct aggregate stock subline and a hint in the
/// product picker). Mirrors <see cref="ShoppingBasketEstimateTests"/>'s shape: a dedicated
/// <see cref="ShoppingListFragmentFactory"/> subclass overrides just the seams this feature touches,
/// reusing the shared fixture and its fakes for everything else.
/// </summary>
public sealed class ShoppingParentItemTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>A parent product id not in the shared fixture — added to the list as a shopping item so
    /// the render path (stock subline + basket price) can be exercised for a parent, alongside the
    /// fixture's untouched leaf items.</summary>
    private static readonly Guid BublyParentId = Guid.Parse("33333333-3333-3333-3333-333333333304");

    /// <summary>Adds "Bubly" (a parent) to the fixture list, registers its catalog summary, and gives it
    /// an aggregate on-hand stock level with <c>IsParent: true</c> — the shape
    /// <c>IShoppingPantryReader.GetStockLevelsAsync</c> already returns for a directly-requested parent id
    /// (plantry-oh27.3). Proves the list renders the parent's aggregate subline exactly like a leaf's.</summary>
    private sealed class ParentItemFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();

                var list = ShoppingListFixture.BuildList();
                list.AddItem(BublyParentId, quantity: 1m, unitId: null, note: null,
                    source: ItemSource.Manual, sourceRef: null, SystemClock.Instance);

                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository>(sp =>
                    new FakeShoppingRepository(sp.GetRequiredService<ITenantContext>(), list));

                services.RemoveAll<IShoppingCatalogReader>();
                var summaries = new Dictionary<Guid, ShoppingProductSummary>(ShoppingListFixture.ProductSummaries())
                {
                    [BublyParentId] = new(BublyParentId, "Bubly", CategoryName: null),
                };
                var candidates = new List<ShoppingProductCandidate>(ShoppingListFixture.ProductCandidates())
                {
                    new(BublyParentId, "Bubly", IsParent: true),
                };
                services.AddSingleton<IShoppingCatalogReader>(new FakeShoppingCatalogReader(
                    summaries, ShoppingListFixture.UnitCodes(), candidates));

                services.RemoveAll<IShoppingPantryReader>();
                var levels = new Dictionary<Guid, ShoppingPantryStockLevel>(ShoppingListFixture.StockLevels())
                {
                    // Aggregate on-hand across two live variants (5 + 2 = 7), rolled up to the parent's
                    // display unit — the number itself isn't re-derived here (that's oh27.2/.3's job); this
                    // proves Shopping renders whatever the port hands it for a parent id.
                    [BublyParentId] = new(BublyParentId, OnHand: 7m, UnitCode: "ea", IsLow: false, IsParent: true),
                };
                services.AddSingleton<IShoppingPantryReader>(new FakeShoppingPantryReaderForSnapshots(levels));
            });
        }
    }

    private static async Task<string> GetPageAsync(ShoppingListFragmentFactory factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact(DisplayName = "A parent product on the list renders with its aggregate on-hand subline")]
    public async Task ParentItem_RendersAggregateStockSubline()
    {
        using var factory = new ParentItemFactory();
        var html = await GetPageAsync(factory);
        var doc = Parser.ParseDocument(html);

        var row = doc.QuerySelectorAll(".sl-item")
            .FirstOrDefault(el => el.TextContent.Contains("Bubly"));
        Assert.NotNull(row);

        var subline = row!.QuerySelector(".sl-instock");
        Assert.NotNull(subline);
        Assert.Contains("7", subline!.TextContent);
        Assert.Contains("ea in pantry", subline.TextContent);
        Assert.DoesNotContain("out", subline.ClassList);
    }

    [Fact(DisplayName = "The product picker renders a parent candidate with an 'any variant' hint")]
    public async Task Picker_RendersParentHint_ForParentCandidate()
    {
        using var factory = new ParentItemFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Shopping?handler=FilterProducts&q=Bubly");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("any variant", html);
        Assert.Contains("Bubly", html);
    }
}
