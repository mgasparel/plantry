using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Unit.Market;

namespace Plantry.Tests.Unit.Market.Prices.Application;

/// <summary>
/// L1 unit tests for <see cref="PriceHistoryRollup"/> (plantry-oh27.5) — the parent-aware price-history
/// projection mirroring <see cref="EffectivePriceRollup"/>'s selection, but returning the full union of
/// live variants' history instead of a single winning candidate. Covers leaf passthrough, parent union
/// across live variants (same unit and cross-unit-converted), skip-on-inconvertible, archived-variant
/// exclusion, deal-source exclusion (inherited from <c>PricingQueries.RawHistoryForProductsAsync</c>'s
/// underlying repository filter), oldest-first-with-product-id-tiebreak ordering, and parity with
/// <see cref="EffectivePriceRollup"/>'s converted unit price for the same observation (guards the shared
/// conversion helper the two rollups both call).
/// </summary>
public sealed class PriceHistoryRollupTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ParentId = Guid.CreateVersion7();
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly Guid GramId = Guid.CreateVersion7();
    private static readonly Guid EachId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();

    private static PriceObservation Purchase(
        Guid productId, decimal price, decimal quantity, Guid unitId, DateTimeOffset observedAt, decimal? unitPrice = null) =>
        PriceObservation.Record(Household, productId, null, price, quantity, unitId, unitPrice,
            PriceSource.Purchase, "Superstore", SourceRef, observedAt, UserId);

    private static PriceObservation Deal(
        Guid productId, decimal price, decimal quantity, Guid unitId, DateTimeOffset observedAt, DateOnly from, DateOnly to) =>
        PriceObservation.Record(Household, productId, null, price, quantity, unitId, price / quantity,
            PriceSource.Deal, "Flyer", SourceRef, observedAt, UserId, validFrom: from, validTo: to);

    private static PriceRollupProduct Parent(params PriceRollupVariant[] variants) =>
        new(ParentId, GramId, IsParent: true, variants);

    private static PriceRollupVariant Live(Guid id, Guid defaultUnit, bool archived = false) => new(id, defaultUnit, archived);

    private static Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> Converter(
        params ((Guid From, Guid To) Key, decimal Factor)[] factors)
    {
        var map = factors.ToDictionary(f => f.Key, f => f.Factor);
        return (productId, amount, from, to, ct) =>
            map.TryGetValue((from, to), out var factor)
                ? Task.FromResult(Result<decimal>.Success(amount * factor))
                : Task.FromResult(Result<decimal>.Failure(Error.Custom("Catalog.UnresolvableConversion", "no path")));
    }

    private static Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> CountingConverter(
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> inner, out List<(Guid, Guid, Guid)> calls)
    {
        var log = new List<(Guid, Guid, Guid)>();
        calls = log;
        return (productId, amount, from, to, ct) =>
        {
            log.Add((productId, from, to));
            return inner(productId, amount, from, to, ct);
        };
    }

    // ── Leaf: identical to PriceHistoryAsync ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Leaf: rollup history equals PriceHistoryAsync, ReferenceUnitId is Guid.Empty")]
    public async Task Leaf_MatchesPriceHistoryAsync()
    {
        var leafId = Guid.CreateVersion7();
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(leafId, 1.80m, 100m, GramId, Day(1), unitPrice: 0.018m));
        repo.Items.Add(Purchase(leafId, 2.00m, 100m, GramId, Day(2), unitPrice: 0.020m));
        var queries = new PricingQueries(repo);
        var product = new PriceRollupProduct(leafId, GramId, IsParent: false, []);

        var expected = await queries.PriceHistoryAsync(leafId);
        var rollup = await PriceHistoryRollup.ForProductAsync(queries, product, Converter());

        Assert.Equal(leafId, rollup.RequestedProductId);
        Assert.Equal(Guid.Empty, rollup.ReferenceUnitId);
        Assert.Equal(expected, rollup.Points);
        Assert.Equal([leafId], rollup.ContributingProductIds);
        Assert.Empty(rollup.SkippedProductIds);
    }

    [Fact(DisplayName = "Leaf: no history — empty points, not contributing")]
    public async Task Leaf_NoHistory_Empty()
    {
        var leafId = Guid.CreateVersion7();
        var repo = new FakePriceObservationRepository();
        var queries = new PricingQueries(repo);
        var product = new PriceRollupProduct(leafId, GramId, IsParent: false, []);

        var rollup = await PriceHistoryRollup.ForProductAsync(queries, product, Converter());

        Assert.Empty(rollup.Points);
        Assert.Empty(rollup.ContributingProductIds);
        Assert.Empty(rollup.SkippedProductIds);
    }

    // ── Parent: union across live variants ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Parent: two variants in the same unit — merged, oldest-first series")]
    public async Task Parent_TwoVariantsSameUnit_MergedOrdered()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(V1, 1.80m, 100m, GramId, Day(2))); // 0.018/g
        repo.Items.Add(Purchase(V2, 2.00m, 100m, GramId, Day(1))); // 0.020/g, earlier
        var queries = new PricingQueries(repo);

        var rollup = await PriceHistoryRollup.ForProductAsync(
            queries, Parent(Live(V1, GramId), Live(V2, GramId)), Converter());

        Assert.Equal(GramId, rollup.ReferenceUnitId);
        Assert.Equal(2, rollup.Points.Count);
        Assert.Equal(DateOnly.FromDateTime(Day(1).UtcDateTime), rollup.Points[0].ObservedAt);
        Assert.Equal(0.020m, rollup.Points[0].UnitPrice); // V2's earlier point first
        Assert.Equal(0.018m, rollup.Points[1].UnitPrice);
        Assert.Equal(new[] { V1, V2 }.OrderBy(id => id), rollup.ContributingProductIds);
        Assert.Empty(rollup.SkippedProductIds);
    }

    [Fact(DisplayName = "Parent: variant in a convertible different unit — converted per parent unit")]
    public async Task Parent_ConvertibleDifferentUnit_Converted()
    {
        var repo = new FakePriceObservationRepository();
        // V1 observed in EachId; parent's default unit is GramId; 1 each = 250 g.
        repo.Items.Add(Purchase(V1, 2.00m, 1m, EachId, Day(1))); // -> 250 g -> 2.00/250 = 0.008/g
        var queries = new PricingQueries(repo);
        var converter = Converter(((EachId, GramId), 250m));

        var rollup = await PriceHistoryRollup.ForProductAsync(queries, Parent(Live(V1, EachId)), converter);

        Assert.Equal(GramId, rollup.ReferenceUnitId);
        var point = Assert.Single(rollup.Points);
        Assert.Equal(0.008m, point.UnitPrice);
        Assert.Equal([V1], rollup.ContributingProductIds);
    }

    [Fact(DisplayName = "Parent: variant in an inconvertible unit — skipped, listed in SkippedProductIds, other variant still contributes")]
    public async Task Parent_InconvertibleVariant_Skipped()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(V1, 1.00m, 1m, EachId, Day(1))); // no path EachId -> GramId
        repo.Items.Add(Purchase(V2, 1.80m, 100m, GramId, Day(2)));
        var queries = new PricingQueries(repo);

        var rollup = await PriceHistoryRollup.ForProductAsync(
            queries, Parent(Live(V1, EachId), Live(V2, GramId)), Converter()); // no conversion paths registered

        var point = Assert.Single(rollup.Points);
        Assert.Equal(0.018m, point.UnitPrice);
        Assert.Equal([V2], rollup.ContributingProductIds);
        Assert.Equal([V1], rollup.SkippedProductIds);
    }

    [Fact(DisplayName = "Parent: archived variant excluded even with cheaper history")]
    public async Task Parent_ArchivedVariantExcluded()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(V1, 0.01m, 100m, GramId, Day(1))); // archived — must not appear
        repo.Items.Add(Purchase(V2, 1.80m, 100m, GramId, Day(2)));
        var queries = new PricingQueries(repo);

        var rollup = await PriceHistoryRollup.ForProductAsync(
            queries, Parent(Live(V2, GramId), Live(V1, GramId, archived: true)), Converter());

        var point = Assert.Single(rollup.Points);
        Assert.Equal(0.018m, point.UnitPrice);
        Assert.Equal([V2], rollup.ContributingProductIds);
    }

    [Fact(DisplayName = "Parent: deal-sourced observations are excluded (same source filter as PriceHistoryAsync)")]
    public async Task Parent_DealSourcedObservations_Excluded()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Deal(V1, 0.90m, 100m, GramId, Day(1), new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 3))); // deal — excluded
        repo.Items.Add(Purchase(V1, 1.80m, 100m, GramId, Day(2))); // purchase — included
        var queries = new PricingQueries(repo);

        var rollup = await PriceHistoryRollup.ForProductAsync(queries, Parent(Live(V1, GramId)), Converter());

        var point = Assert.Single(rollup.Points);
        Assert.Equal(0.018m, point.UnitPrice);
    }

    [Fact(DisplayName = "Parent: same-timestamp points from two variants are ordered by product id for determinism")]
    public async Task Parent_SameTimestamp_TieBrokenByProductId()
    {
        var (first, second) = V1.CompareTo(V2) < 0 ? (V1, V2) : (V2, V1);
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(second, 2.00m, 100m, GramId, Day(1))); // 0.020/g
        repo.Items.Add(Purchase(first, 1.00m, 100m, GramId, Day(1)));  // 0.010/g, same day
        var queries = new PricingQueries(repo);

        // Variants are declared in DESCENDING guid order ("second" before "first") — the opposite of
        // the expected sorted output — so that a mutant deleting the ThenBy(ProductId) tie-break falls
        // back to iteration/insertion order and MUST flip Points[0] to 0.020m in every run, rather than
        // only when insertion order happens to already coincide with guid order (plantry-oh27.5 pass-1
        // critic — the un-flipped arrangement let the mutant survive 13/20 runs).
        var rollup = await PriceHistoryRollup.ForProductAsync(
            queries, Parent(Live(second, GramId), Live(first, GramId)), Converter());

        Assert.Equal(2, rollup.Points.Count);
        Assert.Equal(0.010m, rollup.Points[0].UnitPrice); // "first" product id sorts ahead on the tie
        Assert.Equal(0.020m, rollup.Points[1].UnitPrice);
    }

    [Fact(DisplayName = "Parent: no live variants — empty result even if an orphaned parent observation exists")]
    public async Task Parent_NoLiveVariants_Empty()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(ParentId, 1.80m, 100m, GramId, Day(1)));
        var queries = new PricingQueries(repo);

        var rollup = await PriceHistoryRollup.ForProductAsync(queries, Parent(), Converter());

        Assert.Empty(rollup.Points);
        Assert.Empty(rollup.ContributingProductIds);
        Assert.Empty(rollup.SkippedProductIds);
    }

    // ── Batching ──────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ForProductsAsync: one raw-history round trip covers every parent's variants, not one per parent")]
    public async Task ForProductsAsync_BatchesRawHistoryAcrossParents()
    {
        var parent2 = Guid.CreateVersion7();
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(V1, 1.80m, 100m, GramId, Day(1)));
        repo.Items.Add(Purchase(V2, 2.00m, 100m, GramId, Day(1)));
        var queries = new PricingQueries(repo);

        var products = new List<PriceRollupProduct>
        {
            new(ParentId, GramId, IsParent: true, [Live(V1, GramId)]),
            new(parent2, GramId, IsParent: true, [Live(V2, GramId)]),
        };

        var results = await PriceHistoryRollup.ForProductsAsync(queries, products, Converter());

        Assert.Equal(1, repo.HistoryForProductsCalls);
        Assert.Single(results[ParentId].Points);
        Assert.Single(results[parent2].Points);
    }

    [Fact(DisplayName = "ForProductsAsync: a variant with many same-unit observations issues at most one convert() call per distinct triple")]
    public async Task ForProductsAsync_MemoizesRepeatedConversions()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(V1, 1.00m, 1m, EachId, Day(1)));
        repo.Items.Add(Purchase(V1, 2.00m, 1m, EachId, Day(2)));
        repo.Items.Add(Purchase(V1, 3.00m, 1m, EachId, Day(3)));
        var queries = new PricingQueries(repo);
        var inner = Converter(((EachId, GramId), 250m));
        var counting = CountingConverter(inner, out var calls);

        var rollup = await PriceHistoryRollup.ForProductAsync(queries, Parent(Live(V1, EachId)), counting);

        Assert.Equal(3, rollup.Points.Count);
        Assert.Single(calls); // one (V1, EachId, GramId) round trip, not three
    }

    // ── Parity with EffectivePriceRollup ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Parity: PriceHistoryRollup and EffectivePriceRollup derive the same converted unit price for the same observation")]
    public async Task Parity_SameConvertedUnitPrice_AsEffectivePriceRollup()
    {
        var repo = new FakePriceObservationRepository();
        var observedAt = Day(1);
        repo.Items.Add(Purchase(V1, 2.00m, 1m, EachId, observedAt));
        var queries = new PricingQueries(repo);
        var converter = Converter(((EachId, GramId), 250m));
        var product = Parent(Live(V1, EachId));

        var historyRollup = await PriceHistoryRollup.ForProductAsync(queries, product, converter);

        var observations = new Dictionary<Guid, PriceObservation> { [V1] = repo.Items[0] };
        var effective = await EffectivePriceRollup.SelectFromObservationsAsync(product, observations, converter);

        Assert.NotNull(effective);
        var point = Assert.Single(historyRollup.Points);
        Assert.Equal(effective.ConvertedUnitPrice, point.UnitPrice);
    }

    private static DateTimeOffset Day(int day) => new(2026, 8, day, 12, 0, 0, TimeSpan.Zero);
}
