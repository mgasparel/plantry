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
/// L4 render test for the "Pantry suggestions" strip's parent fold (plantry-oh27.3, acceptance
/// criterion 1): "a parent + 4 variants, parent threshold set, all variants low -&gt; the strip shows
/// exactly one 'Bubly' suggestion." <see cref="ShoppingPantryReaderAdapterTests"/> (Plantry.Tests.Web)
/// already pins the adapter-level tiering that produces this row; this test proves the row actually
/// reaches the rendered <c>#pantry-suggestions</c> strip as exactly one chip, not per-variant chips —
/// the acceptance criterion is about the STRIP, not just the port.
/// </summary>
public sealed class ShoppingPantrySuggestionStripTests
{
    private static readonly HtmlParser Parser = new();
    private static readonly Guid ParentId = Guid.Parse("77777777-7777-7777-7777-000000000001");

    /// <summary>A single parent-shaped Tier-1 restock candidate — mirrors what
    /// <c>ShoppingPantryReaderAdapter.GetLowStockProductsAsync</c> would return for "Bubly" once its
    /// four variants are all low and the parent carries the threshold rule (see the adapter-level test
    /// with the same name in <see cref="ShoppingPantryReaderAdapterTests"/>).</summary>
    private sealed class FakeParentSuggestionPantryReader : IShoppingPantryReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>>(new Dictionary<Guid, ShoppingPantryStockLevel>());

        public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingPantryStockLevel>>(
            [
                new ShoppingPantryStockLevel(
                    ParentId, OnHand: 2m, UnitCode: "ea", IsLow: true, HasLowStockThreshold: true, IsParent: true),
            ]);

        public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetFrequentStapleProductsAsync(
            DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingPantryStockLevel>>([]);
    }

    /// <summary>Resolves only the parent's name — the fixture's other catalog data is irrelevant here
    /// since the pantry reader above surfaces only the parent id.</summary>
    private sealed class FakeParentOnlyCatalogReader : IShoppingCatalogReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(
                new Dictionary<Guid, ShoppingProductSummary>
                {
                    [ParentId] = new(ParentId, "Bubly", "Drinks", CategoryHue: 210),
                });

        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
            IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

        public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(null);

        public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

        public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);

        public Task<IReadOnlyDictionary<Guid, ShoppingProductFamily>> ResolveFamilyAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductFamily>>(new Dictionary<Guid, ShoppingProductFamily>());
    }

    private sealed class ParentSuggestionFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IShoppingPantryReader>();
                services.AddSingleton<IShoppingPantryReader>(new FakeParentSuggestionPantryReader());
                services.RemoveAll<IShoppingCatalogReader>();
                services.AddSingleton<IShoppingCatalogReader>(new FakeParentOnlyCatalogReader());
                services.RemoveAll<PantrySuggestionService>();
                services.AddScoped<PantrySuggestionService>();
            });
        }
    }

    [Fact(DisplayName = "Pantry suggestions strip — a parent with 4 low variants renders exactly one 'Bubly' chip, no per-variant chips")]
    public async Task ParentWithLowVariants_RendersSingleParentChip()
    {
        using var factory = new ParentSuggestionFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var strip = doc.QuerySelector("#pantry-suggestions");
        Assert.NotNull(strip);

        var chips = strip!.QuerySelectorAll(".sl-sug");
        var chip = Assert.Single(chips);
        var name = chip.QuerySelector(".sl-sug-name");
        Assert.NotNull(name);
        Assert.Equal("Bubly", name!.TextContent);
    }

    // ── Family dedup (plantry-oh27.4): a variant already on the list suppresses its parent's suggestion ──

    private static readonly Guid BublyOrangeId = Guid.Parse("77777777-7777-7777-7777-000000000002");

    /// <summary>Extends <see cref="FakeParentOnlyCatalogReader"/>'s summary map with the variant's name
    /// and resolves the variant's family (ParentId = <see cref="ParentId"/>) — the fact
    /// <c>Index.cshtml.cs::ExpandFamilyAsync</c> needs to fold "Bubly Orange is on the list" into
    /// "Bubly is on the list" for suggestion dedup.</summary>
    private sealed class FakeVariantFamilyCatalogReader : IShoppingCatalogReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(
                new Dictionary<Guid, ShoppingProductSummary>
                {
                    [ParentId] = new(ParentId, "Bubly", "Drinks", CategoryHue: 210),
                    [BublyOrangeId] = new(BublyOrangeId, "Bubly Orange", "Drinks", CategoryHue: 210),
                });

        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
            IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

        public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(null);

        public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

        public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);

        public Task<IReadOnlyDictionary<Guid, ShoppingProductFamily>> ResolveFamilyAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, ShoppingProductFamily> result = productIds.Contains(BublyOrangeId)
                ? new Dictionary<Guid, ShoppingProductFamily>
                {
                    [BublyOrangeId] = new(BublyOrangeId, Guid.Empty, IsParent: false, ParentId: ParentId, Variants: []),
                }
                : new Dictionary<Guid, ShoppingProductFamily>();
            return Task.FromResult(result);
        }
    }

    private sealed class VariantOnListSuppressesParentFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var list = ShoppingList.Create(HouseholdId.From(ShoppingListFixture.HouseholdAId), SystemClock.Instance);
                list.AddItem(BublyOrangeId, quantity: 1m, unitId: null, note: null,
                    source: ItemSource.Manual, sourceRef: null, SystemClock.Instance);

                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository>(sp =>
                    new FakeShoppingRepository(sp.GetRequiredService<ITenantContext>(), list));

                services.RemoveAll<IShoppingPantryReader>();
                services.AddSingleton<IShoppingPantryReader>(new FakeParentSuggestionPantryReader());
                services.RemoveAll<IShoppingCatalogReader>();
                services.AddSingleton<IShoppingCatalogReader>(new FakeVariantFamilyCatalogReader());
                services.RemoveAll<PantrySuggestionService>();
                services.AddScoped<PantrySuggestionService>();
            });
        }
    }

    [Fact(DisplayName = "Pantry suggestions strip — 'Bubly Orange' already on the list suppresses the 'Bubly' parent suggestion (family dedup)")]
    public async Task VariantOnList_SuppressesParentSuggestion()
    {
        using var factory = new VariantOnListSuppressesParentFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var strip = doc.QuerySelector("#pantry-suggestions");
        Assert.NotNull(strip);

        // Without the family fold, "Bubly" (the parent) would still surface as a Tier-1 suggestion
        // (FakeParentSuggestionPantryReader always returns it) — the fold must exclude it because its
        // variant "Bubly Orange" is on the list.
        var chips = strip!.QuerySelectorAll(".sl-sug");
        Assert.Empty(chips);
    }

    // ── Family dedup, the OTHER direction: a parent already on the list suppresses a variant's suggestion ──

    /// <summary>A single VARIANT-shaped Tier-1 restock candidate for <see cref="BublyOrangeId"/> — mirrors
    /// what a ruleless variant with its OWN low stock (rather than its parent standing in) would surface as,
    /// so this test exercises the opposite fold direction from <see cref="FakeParentSuggestionPantryReader"/>.</summary>
    private sealed class FakeVariantSuggestionPantryReader : IShoppingPantryReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>>(new Dictionary<Guid, ShoppingPantryStockLevel>());

        public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingPantryStockLevel>>(
            [
                new ShoppingPantryStockLevel(
                    BublyOrangeId, OnHand: 1m, UnitCode: "ea", IsLow: true, HasLowStockThreshold: true, IsParent: false),
            ]);

        public Task<IReadOnlyList<ShoppingPantryStockLevel>> GetFrequentStapleProductsAsync(
            DateOnly today, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingPantryStockLevel>>([]);
    }

    /// <summary>Resolves <see cref="ParentId"/>'s family to include <see cref="BublyOrangeId"/> as a live
    /// variant — the fact <c>Index.cshtml.cs::ExpandFamilyAsync</c> needs to fold "Bubly (the parent) is on
    /// the list" into "Bubly Orange (its variant) is on the list" for suggestion dedup, the reverse fold
    /// from <see cref="FakeVariantFamilyCatalogReader"/>.</summary>
    private sealed class FakeParentFamilyCatalogReader : IShoppingCatalogReader
    {
        public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShoppingProductSummary>>(
                new Dictionary<Guid, ShoppingProductSummary>
                {
                    [ParentId] = new(ParentId, "Bubly", "Drinks", CategoryHue: 210),
                    [BublyOrangeId] = new(BublyOrangeId, "Bubly Orange", "Drinks", CategoryHue: 210),
                });

        public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
            IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

        public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(null);

        public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

        public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>([]);

        public Task<IReadOnlyDictionary<Guid, ShoppingProductFamily>> ResolveFamilyAsync(
            IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<Guid, ShoppingProductFamily> result = productIds.Contains(ParentId)
                ? new Dictionary<Guid, ShoppingProductFamily>
                {
                    [ParentId] = new(ParentId, Guid.Empty, IsParent: true, ParentId: null,
                        Variants: [new ShoppingFamilyVariant(BublyOrangeId, Guid.Empty)]),
                }
                : new Dictionary<Guid, ShoppingProductFamily>();
            return Task.FromResult(result);
        }
    }

    private sealed class ParentOnListSuppressesVariantFactory : ShoppingListFragmentFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var list = ShoppingList.Create(HouseholdId.From(ShoppingListFixture.HouseholdAId), SystemClock.Instance);
                list.AddItem(ParentId, quantity: 1m, unitId: null, note: null,
                    source: ItemSource.Manual, sourceRef: null, SystemClock.Instance);

                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository>(sp =>
                    new FakeShoppingRepository(sp.GetRequiredService<ITenantContext>(), list));

                services.RemoveAll<IShoppingPantryReader>();
                services.AddSingleton<IShoppingPantryReader>(new FakeVariantSuggestionPantryReader());
                services.RemoveAll<IShoppingCatalogReader>();
                services.AddSingleton<IShoppingCatalogReader>(new FakeParentFamilyCatalogReader());
                services.RemoveAll<PantrySuggestionService>();
                services.AddScoped<PantrySuggestionService>();
            });
        }
    }

    [Fact(DisplayName = "Pantry suggestions strip — the parent 'Bubly' already on the list suppresses a variant's own suggestion (family dedup, reverse direction)")]
    public async Task ParentOnList_SuppressesVariantSuggestion()
    {
        using var factory = new ParentOnListSuppressesVariantFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, ShoppingListFixture.HouseholdAId.ToString());

        var response = await client.GetAsync("/Shopping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var strip = doc.QuerySelector("#pantry-suggestions");
        Assert.NotNull(strip);

        // Without the family fold, "Bubly Orange" (the variant) would still surface as a Tier-1 suggestion
        // (FakeVariantSuggestionPantryReader always returns it) — the fold must exclude it because its
        // parent "Bubly" is on the list.
        var chips = strip!.QuerySelectorAll(".sl-sug");
        Assert.Empty(chips);
    }
}
