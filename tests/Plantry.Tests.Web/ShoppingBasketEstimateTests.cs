using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Planning.Application;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render tests for the shopping-list basket cost estimate (plantry-e016, stats-injection appendix).
/// Proves the summary line renders the estimated total (single figure when exact, a range when some lines
/// are quantity/unit-uncertain), the active-deals chip, and the unpriced footnote — driven purely by what
/// <see cref="IShoppingPriceReader"/> returns, mirroring <see cref="ShoppingDealBadgeTests"/>'s shape for the
/// sibling deal-badge feature.
/// </summary>
public sealed class ShoppingBasketEstimateTests
{
    private static readonly HtmlParser Parser = new();

    /// <summary>A unit id that never appears on any fixture item — pairing a price observation with this id
    /// guarantees <c>item.UnitId != estimate.UnitId</c>, and the shared factory's <c>FakeShoppingCatalogReader</c>
    /// always returns null from <c>TryConvertAsync</c> (no conversion path in snapshot tests), so a line priced
    /// under this unit always lands in <see cref="Plantry.Planning.Domain.BasketCostEstimate"/>'s uncertain
    /// (high-bound-only) bucket.</summary>
    private static readonly Guid UnconvertibleUnitId = Guid.Parse("77777777-7777-7777-7777-777777777701");

    /// <summary>Prices the fixture's Milk (qty 2, same unit as the observation) at $2.00/unit — an exact
    /// $4.00 line. Chicken is left unpriced (not registered), and Sriracha is free-text (never priceable) —
    /// both unchecked, so the estimate's unpriced footnote counts 2. Flour is checked in the fixture and is
    /// excluded from the estimate scope entirely (already bought). Reuses the shared factory's
    /// <see cref="FakeShoppingPriceReaderForSnapshots"/> rather than a bespoke fake (gate 10 — reuse first).</summary>
    private sealed class EstimateFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();
                services.RemoveAll<IShoppingPriceReader>();
                services.AddSingleton<IShoppingPriceReader>(new FakeShoppingPriceReaderForSnapshots(
                    new Dictionary<Guid, ShoppingPriceEstimate>
                    {
                        [ShoppingListFixture.MilkProductId] = new(
                            ShoppingListFixture.MilkProductId, Price: 2.00m, Quantity: 1m, UnitId: ShoppingListFixture.UnitId),
                    }));
            });
        }
    }

    /// <summary>Prices Milk exactly (as <see cref="EstimateFactory"/>) and Chicken under
    /// <see cref="UnconvertibleUnitId"/> — a real price with no conversion path onto Chicken's own unit, which
    /// <see cref="Plantry.Planning.Domain.ShoppingBasketCostingService"/> treats as high-bound-only uncertain.
    /// Sriracha (free-text) still footnotes as unpriced, so this also proves the range and the footnote
    /// coexist correctly.</summary>
    private sealed class RangeEstimateFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeExpiringSoonHorizon();
                services.RemoveAll<IShoppingPriceReader>();
                services.AddSingleton<IShoppingPriceReader>(new FakeShoppingPriceReaderForSnapshots(
                    new Dictionary<Guid, ShoppingPriceEstimate>
                    {
                        [ShoppingListFixture.MilkProductId] = new(
                            ShoppingListFixture.MilkProductId, Price: 2.00m, Quantity: 1m, UnitId: ShoppingListFixture.UnitId),
                        [ShoppingListFixture.ChickenProductId] = new(
                            ShoppingListFixture.ChickenProductId, Price: 6.00m, Quantity: 1m, UnitId: UnconvertibleUnitId),
                    }));
            });
        }
    }

    /// <summary>Registers an active deal for Milk only, so <see cref="ShoppingListFixture.ChickenProductId"/>
    /// stays undealt — proving the singular "1 item on active deals" wording (not just the plural default) and
    /// that the count reflects only unchecked, deal-covered items.</summary>
    private sealed class MilkDealFactory : ShoppingListFragmentFactory
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

    private sealed class FakeDealReaderWithMilkDeal : IShoppingDealReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingActiveDeal>> GetActiveDealsAsync(
            IReadOnlyList<Guid> productIds, DateOnly today, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, ShoppingActiveDeal> result = new Dictionary<Guid, ShoppingActiveDeal>();
            if (productIds.Contains(ShoppingListFixture.MilkProductId))
            {
                result = new Dictionary<Guid, ShoppingActiveDeal>
                {
                    [ShoppingListFixture.MilkProductId] = new(
                        ShoppingListFixture.MilkProductId, Guid.NewGuid(), Guid.NewGuid(), "FreshCo"),
                };
            }
            return Task.FromResult(result);
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

    [Fact(DisplayName = "Estimate — priced + unpriced items on the list: shows the estimated total and the unpriced footnote")]
    public async Task PricedAndUnpricedItems_ShowsEstimateAndUnpricedFootnote()
    {
        using var factory = new EstimateFactory();
        var html = await GetPageAsync(factory);
        var doc = Parser.ParseDocument(html);

        var summary = doc.QuerySelector("#sl-summary");
        Assert.NotNull(summary);

        var estimate = summary!.QuerySelector(".sl-est-amount");
        Assert.NotNull(estimate);
        Assert.Contains("$4.00", estimate!.TextContent); // Milk: qty 2 × $2.00/unit, exact (no range)

        var unpriced = summary.QuerySelector(".sl-est-unpriced");
        Assert.NotNull(unpriced);
        // Chicken + Sriracha (both unchecked, unpriced). Flour is checked — excluded from the estimate scope.
        Assert.Contains("+2 unpriced items", unpriced!.TextContent);
    }

    [Fact(DisplayName = "Estimate — no price history anywhere on the list: no estimate amount rendered, only the unpriced footnote")]
    public async Task NoPriceHistoryAnywhere_NoEstimateAmountRendered()
    {
        // The shared factory's default IShoppingPriceReader (FakeShoppingPriceReaderForSnapshots) resolves
        // no prices at all — every item on the fixture list footnotes as unpriced.
        using var factory = new ShoppingListFragmentFactory();
        var html = await GetPageAsync(factory);
        var doc = Parser.ParseDocument(html);

        var summary = doc.QuerySelector("#sl-summary");
        Assert.NotNull(summary);
        Assert.Null(summary!.QuerySelector(".sl-est-amount"));

        var unpriced = summary.QuerySelector(".sl-est-unpriced");
        Assert.NotNull(unpriced);
        // Milk, Chicken, Sriracha — all unchecked and unpriced. Flour is checked, excluded from scope.
        Assert.Contains("+3 unpriced items", unpriced!.TextContent);
    }

    [Fact(DisplayName = "Estimate — a quantity/unit-uncertain line renders a low–high range, not a single figure")]
    public async Task UncertainLine_RendersRange()
    {
        using var factory = new RangeEstimateFactory();
        var html = await GetPageAsync(factory);
        var doc = Parser.ParseDocument(html);

        var summary = doc.QuerySelector("#sl-summary");
        Assert.NotNull(summary);

        var estimate = summary!.QuerySelector(".sl-est-amount");
        Assert.NotNull(estimate);
        // Low = Milk's exact $4.00 line only; High = Low + Chicken's uncertain $6.00 pack price = $10.00.
        Assert.Contains("$4.00", estimate!.TextContent);
        Assert.Contains("$10.00", estimate.TextContent);
        Assert.Contains("–", estimate.TextContent); // en dash separates low and high

        var unpriced = summary.QuerySelector(".sl-est-unpriced");
        Assert.NotNull(unpriced);
        Assert.Contains("+1 unpriced item", unpriced!.TextContent); // Sriracha only
    }

    [Fact(DisplayName = "Estimate — an item on an active deal renders the singular '1 item on active deals' chip")]
    public async Task ItemOnActiveDeal_RendersSingularDealsChip()
    {
        using var factory = new MilkDealFactory();
        var html = await GetPageAsync(factory);
        var doc = Parser.ParseDocument(html);

        var summary = doc.QuerySelector("#sl-summary");
        Assert.NotNull(summary);

        var deals = summary!.QuerySelector(".sl-est-deals");
        Assert.NotNull(deals);
        Assert.Contains("1 item on active deals", deals!.TextContent);
    }
}
