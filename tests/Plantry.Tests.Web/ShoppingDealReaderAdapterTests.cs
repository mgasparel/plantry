using Plantry.Catalog.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Shopping;

namespace Plantry.Tests.Web;

/// <summary>
/// L2 unit tests for <see cref="ShoppingDealReaderAdapter"/> (P5-9) — the Shopping→Pricing ACL adapter.
/// Proves the adapter surfaces Pricing's cheapest-active-deal read model (deal id from the observation's
/// source ref) and resolves the merchant name over Catalog's store repository — Shopping reads Pricing +
/// Catalog, never Deals (ADR-010). The window/MIN filtering itself lives in Pricing and is proven in the
/// Pricing suite (PricingQueriesTests / PricingRepositoryTests); here we pin the adapter's read + mapping.
/// </summary>
public sealed class ShoppingDealReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly DateOnly Today = new(2026, 7, 4);

    private static PriceObservation Deal(Guid productId, Guid dealId, Guid? storeId, decimal unitPrice) =>
        PriceObservation.Record(
            Household, productId, null, price: unitPrice, quantity: 1m, unitId: Guid.CreateVersion7(),
            unitPrice: unitPrice, source: PriceSource.Deal, merchantText: "Flyer",
            sourceRef: dealId, observedAt: DateTimeOffset.UtcNow, userId: Guid.CreateVersion7(),
            validFrom: new(2026, 7, 1), validTo: new(2026, 7, 7), storeId: storeId);

    [Fact(DisplayName = "Adapter surfaces the cheapest active deal with the resolved store name")]
    public async Task Surfaces_ActiveDeal_With_StoreName()
    {
        var productId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();

        // A real store so its generated id can be threaded onto the observation.
        var store = Store.Create(Household, "FreshCo", Clock);
        var storeRepo = new FakeStoreRepository([store]);

        var priceRepo = new FakePriceObservationRepository();
        priceRepo.RegisterActiveDeal(productId, Today, Deal(productId, dealId, store.Id.Value, 2.49m));

        var adapter = new ShoppingDealReaderAdapter(new PricingQueries(priceRepo), storeRepo);

        var result = await adapter.GetActiveDealsAsync([productId], Today);

        var deal = Assert.Contains(productId, result);
        Assert.Equal(dealId, deal.DealId);
        Assert.Equal(store.Id.Value, deal.StoreId);
        Assert.Equal("FreshCo", deal.StoreName);
    }

    [Fact(DisplayName = "Adapter omits products with no active deal")]
    public async Task Omits_Products_With_No_Active_Deal()
    {
        var productId = Guid.CreateVersion7();
        var priceRepo = new FakePriceObservationRepository(); // no deals registered
        var adapter = new ShoppingDealReaderAdapter(
            new PricingQueries(priceRepo), new FakeStoreRepository([]));

        var result = await adapter.GetActiveDealsAsync([productId], Today);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "Adapter returns a storeless deal (null StoreName) when the observation carries no store")]
    public async Task Deal_Without_Store_Has_Null_StoreName()
    {
        var productId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var priceRepo = new FakePriceObservationRepository();
        priceRepo.RegisterActiveDeal(productId, Today, Deal(productId, dealId, storeId: null, unitPrice: 3.00m));

        var adapter = new ShoppingDealReaderAdapter(
            new PricingQueries(priceRepo), new FakeStoreRepository([]));

        var result = await adapter.GetActiveDealsAsync([productId], Today);

        var deal = Assert.Contains(productId, result);
        Assert.Equal(dealId, deal.DealId);
        Assert.Null(deal.StoreId);
        Assert.Null(deal.StoreName);
    }

    [Fact(DisplayName = "Adapter returns a deal with a null StoreName when the store id is unresolved in Catalog")]
    public async Task Deal_With_Unknown_Store_Has_Null_StoreName()
    {
        var productId = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var orphanStoreId = Guid.CreateVersion7(); // not present in Catalog

        var priceRepo = new FakePriceObservationRepository();
        priceRepo.RegisterActiveDeal(productId, Today, Deal(productId, dealId, orphanStoreId, 3.00m));

        var adapter = new ShoppingDealReaderAdapter(
            new PricingQueries(priceRepo), new FakeStoreRepository([]));

        var result = await adapter.GetActiveDealsAsync([productId], Today);

        var deal = Assert.Contains(productId, result);
        Assert.Equal(orphanStoreId, deal.StoreId);
        Assert.Null(deal.StoreName);
    }

    [Fact(DisplayName = "Adapter short-circuits on an empty product list")]
    public async Task Empty_ProductList_Returns_Empty()
    {
        var adapter = new ShoppingDealReaderAdapter(
            new PricingQueries(new FakePriceObservationRepository()), new FakeStoreRepository([]));

        var result = await adapter.GetActiveDealsAsync([], Today);

        Assert.Empty(result);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="IPriceObservationRepository"/>. Only the cheapest-active-deal read is exercised
    /// here; the DB applies the window/MIN filter, so the fake returns exactly what a test registers for a
    /// (product, today) pair — the adapter under test does no window logic of its own.
    /// </summary>
    private sealed class FakePriceObservationRepository : IPriceObservationRepository
    {
        private readonly Dictionary<(Guid Product, DateOnly Today), PriceObservation> _activeDeals = [];

        public void RegisterActiveDeal(Guid productId, DateOnly today, PriceObservation observation) =>
            _activeDeals[(productId, today)] = observation;

        public Task<PriceObservation?> CheapestActiveDealForProductAsync(
            Guid productId, DateOnly today, CancellationToken ct = default) =>
            Task.FromResult(_activeDeals.TryGetValue((productId, today), out var obs) ? obs : null);

        public Task AddAsync(PriceObservation observation, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default) =>
            Task.FromResult<PriceObservation?>(null);
        public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
            Task.FromResult<PriceObservation?>(null);
        public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PriceObservation>>([]);
    }

    /// <summary>In-memory <see cref="IStoreRepository"/> over a fixed store list.</summary>
    private sealed class FakeStoreRepository(List<Store> stores) : IStoreRepository
    {
        public Task<List<Store>> ListAsync(CancellationToken ct = default) => Task.FromResult(stores);
        public Task<Store?> FindAsync(StoreId id, CancellationToken ct = default) =>
            Task.FromResult(stores.FirstOrDefault(s => s.Id == id));
        public Task<Store?> FindByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(stores.FirstOrDefault(s => s.Name == name));
        public Task<Store?> FindByExternalRefAsync(string externalRef, CancellationToken ct = default) =>
            Task.FromResult(stores.FirstOrDefault(s => s.ExternalRef == externalRef));
        public Task<List<Store>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(stores.Where(s => !s.IsArchived).ToList());
        public Task AddAsync(Store store, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
