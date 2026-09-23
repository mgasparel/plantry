using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Planning.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Shopping.Application;
using Plantry.Web.Shopping;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="ShoppingPriceReaderAdapter"/> (plantry-oh27.4) — the Shopping→Pricing ACL
/// adapter's parent-aware rollup path, mirroring <c>MealPlanPriceReaderAdapter</c>'s use of
/// <see cref="EffectivePriceRollup"/>. Covers: a leaf id resolves via the direct
/// <see cref="PricingQueries.EffectiveCostablePricesAsync"/> path unchanged; a parent with two
/// differently-priced live variants resolves to the cheaper one, converted into the parent's default
/// unit; a variant whose conversion fails is skipped rather than shadowing the parent; and an id absent
/// from the family map falls through to the leaf path. Uses a real <see cref="PricingQueries"/> over a
/// minimal in-memory <see cref="IPriceObservationRepository"/> (mirrors <c>DealAwareCostingAdapterTests</c>'s
/// <c>WindowAwarePriceRepo</c> shape) rather than a bespoke price stub, so the adapter is exercised against
/// its real collaborator, not a hand-rolled substitute for it.
/// </summary>
public sealed class ShoppingPriceReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly DateOnly Today = new(2026, 8, 1);
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero); // < Today
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid GramId = Guid.CreateVersion7();
    private static readonly Guid EachId = Guid.CreateVersion7();

    private static PriceObservation Purchase(Guid productId, decimal price, decimal quantity, Guid unitId) =>
        PriceObservation.Record(Household, productId, null, price, quantity, unitId, unitPrice: null,
            PriceSource.Purchase, "Store", null, ObservedAt, UserId);

    private static ShoppingPriceReaderAdapter Adapter(FakePriceObservationRepository repo, FakeShoppingCatalogReader catalog) =>
        new(new PricingQueries(repo), catalog);

    [Fact(DisplayName = "A leaf id resolves via the direct (non-rollup) path, unchanged")]
    public async Task LeafId_ResolvesDirect()
    {
        var leafId = Guid.CreateVersion7();
        var repo = new FakePriceObservationRepository();
        repo.Add(Purchase(leafId, price: 4.00m, quantity: 2m, EachId));
        var catalog = new FakeShoppingCatalogReader(); // ResolveFamilyAsync returns nothing for leafId

        var result = await Adapter(repo, catalog).GetEffectivePricesAsync([leafId], Today);

        var estimate = result[leafId];
        Assert.Equal(4.00m, estimate.Price);
        Assert.Equal(2m, estimate.Quantity);
        Assert.Equal(EachId, estimate.UnitId);
    }

    [Fact(DisplayName = "A parent with two differently-priced live variants resolves to the cheaper one, converted into the parent's default unit")]
    public async Task ParentId_ResolvesCheapestVariant_ConvertedToParentUnit()
    {
        var parentId = Guid.CreateVersion7();
        var cheapRawButWorseConverted = Guid.CreateVersion7();
        var pricierRawButBetterConverted = Guid.CreateVersion7();

        // Both variants observe in GramId; the parent's default (reference) unit is EachId — every
        // candidate MUST go through a real conversion for this test to prove anything. The two variants'
        // conversion factors are chosen so the RAW price ordering is the OPPOSITE of the CONVERTED
        // unit-price ordering — a test that only used same-unit observations (raw == converted) could
        // pass even if the adapter mapped the winner's raw observation instead of the rollup's converted
        // values, which is exactly the mutation this test pins.
        var repo = new FakePriceObservationRepository();
        // $2.00 for 1 g, but 1 g converts to only 0.1 "each" for this variant -> converted unit price
        // 2.00 / 0.1 = $20/each. Cheaper raw price, but the worse deal once converted.
        repo.Add(Purchase(cheapRawButWorseConverted, price: 2.00m, quantity: 1m, GramId));
        // $5.00 for 1 g, and 1 g converts to 1 "each" for this variant -> converted unit price
        // 5.00 / 1 = $5/each. Pricier raw price, but the winning deal once converted.
        repo.Add(Purchase(pricierRawButBetterConverted, price: 5.00m, quantity: 1m, GramId));

        var catalog = new FakeShoppingCatalogReader();
        catalog.RegisterConversion(GramId, EachId, cheapRawButWorseConverted, convertedAmount: 0.1m);
        catalog.RegisterConversion(GramId, EachId, pricierRawButBetterConverted, convertedAmount: 1m);
        catalog.RegisterFamily(new ShoppingProductFamily(
            parentId, EachId, IsParent: true, ParentId: null,
            Variants:
            [
                new ShoppingFamilyVariant(cheapRawButWorseConverted, GramId),
                new ShoppingFamilyVariant(pricierRawButBetterConverted, GramId),
            ]));

        var result = await Adapter(repo, catalog).GetEffectivePricesAsync([parentId], Today);

        var estimate = result[parentId];
        // The RAW-cheaper variant loses: $20/each converted is worse than $5/each converted — proves the
        // winner is chosen on the CONVERTED unit price, not the raw observation price.
        Assert.Equal(5.00m, estimate.Price);
        // Quantity/unit are the ROLLUP'S CONVERTED values (1 g * factor 1 = 1 each), not the raw
        // observation's own quantity/unit (which would also happen to be "1", but in GramId) — pinned by
        // asserting UnitId is the parent's EachId, never GramId.
        Assert.Equal(1m, estimate.Quantity);
        Assert.Equal(EachId, estimate.UnitId);
    }

    [Fact(DisplayName = "A variant whose conversion into the parent unit fails is skipped, not shadowing a usable candidate")]
    public async Task VariantWithNoConversionPath_IsSkipped()
    {
        var parentId = Guid.CreateVersion7();
        var unconvertibleVariant = Guid.CreateVersion7();
        var usableVariant = Guid.CreateVersion7();
        var otherUnitId = Guid.CreateVersion7(); // no registered conversion to GramId

        var repo = new FakePriceObservationRepository();
        // This variant is technically cheaper per its own unit, but has no path to the parent's GramId —
        // it must be skipped rather than winning by default.
        repo.Add(Purchase(unconvertibleVariant, price: 1.00m, quantity: 1m, otherUnitId));
        repo.Add(Purchase(usableVariant, price: 9.00m, quantity: 1m, GramId));

        var catalog = new FakeShoppingCatalogReader();
        catalog.RegisterFamily(new ShoppingProductFamily(
            parentId, GramId, IsParent: true, ParentId: null,
            Variants:
            [
                new ShoppingFamilyVariant(unconvertibleVariant, otherUnitId),
                new ShoppingFamilyVariant(usableVariant, GramId),
            ]));
        // No RegisterConversion call for (otherUnitId, GramId, unconvertibleVariant) — TryConvertAsync
        // returns null for it, so EffectivePriceRollup must skip that candidate.

        var result = await Adapter(repo, catalog).GetEffectivePricesAsync([parentId], Today);

        var estimate = result[parentId];
        Assert.Equal(9.00m, estimate.Price); // the only usable candidate wins, despite being pricier
        Assert.Equal(GramId, estimate.UnitId);
    }

    [Fact(DisplayName = "An id absent from the catalog family map (e.g. a deleted product) falls through to the leaf path")]
    public async Task UnknownId_FallsThroughToLeafPath()
    {
        var unknownId = Guid.CreateVersion7();
        var repo = new FakePriceObservationRepository();
        repo.Add(Purchase(unknownId, price: 3.00m, quantity: 1m, EachId));
        var catalog = new FakeShoppingCatalogReader(); // ResolveFamilyAsync returns nothing for unknownId

        var result = await Adapter(repo, catalog).GetEffectivePricesAsync([unknownId], Today);

        Assert.Equal(3.00m, result[unknownId].Price);
    }

    [Fact(DisplayName = "Multiple parents on the basket resolve from ONE batched observation fetch, not one per parent")]
    public async Task MultipleParents_ShareOneBatchedObservationFetch()
    {
        var parent1 = Guid.CreateVersion7();
        var parent1Variant = Guid.CreateVersion7();
        var parent2 = Guid.CreateVersion7();
        var parent2Variant = Guid.CreateVersion7();

        var repo = new FakePriceObservationRepository();
        repo.Add(Purchase(parent1Variant, price: 3.00m, quantity: 1m, GramId));
        repo.Add(Purchase(parent2Variant, price: 6.00m, quantity: 1m, GramId));

        var catalog = new FakeShoppingCatalogReader();
        catalog.RegisterFamily(new ShoppingProductFamily(
            parent1, GramId, IsParent: true, ParentId: null, Variants: [new ShoppingFamilyVariant(parent1Variant, GramId)]));
        catalog.RegisterFamily(new ShoppingProductFamily(
            parent2, GramId, IsParent: true, ParentId: null, Variants: [new ShoppingFamilyVariant(parent2Variant, GramId)]));

        var result = await Adapter(repo, catalog).GetEffectivePricesAsync([parent1, parent2], Today);

        Assert.Equal(3.00m, result[parent1].Price);
        Assert.Equal(6.00m, result[parent2].Price);
        // Exactly ONE batched LatestForProductsAsync call was made, and it covered BOTH parents'
        // variants together. The rejected per-parent EffectivePriceRollup.SelectAsync shape (each parent
        // issuing its OWN EffectiveCostablePricesAsync call) would also leave LatestForProductCallCounts
        // at 1-per-variant (per-parent batching of a single-variant parent looks identical to shared
        // batching) — that is exactly why this assertion was previously insufficient. Asserting on the
        // BATCH call count/contents, not the per-product call counts, is what actually distinguishes
        // "one shared fetch" from "one fetch per parent".
        Assert.Single(repo.LatestForProductsBatchCalls);
        var batchIds = repo.LatestForProductsBatchCalls[0];
        Assert.Contains(parent1Variant, batchIds);
        Assert.Contains(parent2Variant, batchIds);
    }
}

/// <summary>
/// Minimal in-memory <see cref="IPriceObservationRepository"/> for <see cref="ShoppingPriceReaderAdapterTests"/>
/// — every product's "effective" price is simply its latest Purchase observation (no deals registered), so
/// <see cref="PricingQueries.EffectiveCostablePricesAsync"/> falls straight through to
/// <see cref="LatestForProductAsync"/> for every id, mirroring <c>DealAwareCostingAdapterTests</c>'s
/// <c>WindowAwarePriceRepo</c> but without the deal-window machinery this suite doesn't exercise.
/// Records per-product call counts on the single-product methods so a test can assert the adapter never
/// bypasses the interface's batched default (<c>LatestForProductsAsync</c>/<c>CheapestActiveDealsForProductsAsync</c>
/// loop each id through these exactly once per <see cref="PricingQueries.EffectiveCostablePricesAsync"/> call).
/// </summary>
internal sealed class FakePriceObservationRepository : IPriceObservationRepository
{
    private readonly List<PriceObservation> _items = [];
    public Dictionary<Guid, int> LatestForProductCallCounts { get; } = [];

    /// <summary>Each batched <see cref="LatestForProductsAsync"/> call's requested id set, in call order —
    /// the shape that actually distinguishes "one shared fetch across every parent" from "one fetch per
    /// parent" (see <see cref="ShoppingPriceReaderAdapterTests.MultipleParents_ShareOneBatchedObservationFetch"/>).
    /// Per-product call counts alone cannot tell the two apart when every parent has exactly one variant.</summary>
    public List<IReadOnlyList<Guid>> LatestForProductsBatchCalls { get; } = [];

    public void Add(PriceObservation observation) => _items.Add(observation);

    public Task AddAsync(PriceObservation observation, CancellationToken ct = default)
    {
        _items.Add(observation);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<PriceObservation?> FindAsync(PriceObservationId id, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<PriceObservation>> ListPurchasesAwaitingStoreAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceObservation>>([]);

    public Task<PriceObservation?> LatestForProductAsync(Guid productId, CancellationToken ct = default)
    {
        LatestForProductCallCounts[productId] = LatestForProductCallCounts.GetValueOrDefault(productId) + 1;
        return Task.FromResult(_items
            .Where(o => o.ProductId == productId
                && (o.Source == PriceSource.Purchase || o.Source == PriceSource.Manual)
                && o.SupersededById is null)
            .OrderByDescending(o => o.ObservedAt)
            .FirstOrDefault());
    }

    public Task<PriceObservation?> LatestForSkuAsync(Guid skuId, CancellationToken ct = default) =>
        Task.FromResult<PriceObservation?>(null);

    /// <summary>Explicitly overrides the interface's default per-id loop so a test can distinguish ONE
    /// batched call covering many ids from many single-id calls that happen to add up to the same total
    /// (see <see cref="LatestForProductsBatchCalls"/>).</summary>
    public async Task<IReadOnlyDictionary<Guid, PriceObservation>> LatestForProductsAsync(
        IEnumerable<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().ToList();
        LatestForProductsBatchCalls.Add(ids);

        var result = new Dictionary<Guid, PriceObservation>();
        foreach (var id in ids)
        {
            var observation = await LatestForProductAsync(id, ct);
            if (observation is not null)
                result[id] = observation;
        }
        return result;
    }

    public Task<PriceObservation?> CheapestActiveDealForProductAsync(
        Guid productId, DateOnly today, CancellationToken ct = default) =>
        Task.FromResult<PriceObservation?>(null); // no deals registered in this suite

    public Task<PriceObservation?> ActiveDealForPurchaseAsync(
        Guid productId, Guid storeId, DateOnly observedDate, decimal purchaseUnitPrice, decimal tolerance, CancellationToken ct = default) =>
        Task.FromResult<PriceObservation?>(null);

    public Task<IReadOnlyList<PriceObservation>> HistoryForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceObservation>>(_items
            .Where(o => o.ProductId == productId
                && (o.Source == PriceSource.Purchase || o.Source == PriceSource.Manual)
                && o.SupersededById is null)
            .OrderBy(o => o.ObservedAt)
            .ToList());

    public Task<IReadOnlySet<Guid>> ProductIdsWithAnyObservationAsync(IEnumerable<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(_items.Select(o => o.ProductId).ToHashSet());
}
