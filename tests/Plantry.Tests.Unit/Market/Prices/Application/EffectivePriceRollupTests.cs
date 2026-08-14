using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Market.Prices.Application;

/// <summary>
/// L1 unit tests for the shared parent-aware price rollup <see cref="EffectivePriceRollup"/> (plantry-i07l):
/// the parent/variant aggregation rules (DM-19) every consumer — recipe costing, meal-plan costing, parent
/// price display, and Tidy Up D5 — now feeds through. Covers rule 2 (live direct variants only), rule 3
/// (convert each usable candidate to the requested/reference unit), rule 4 (cheapest remaining candidate
/// wins; none remaining = unpriced), and rule 6 (a concrete leaf resolves to itself, no forced conversion).
/// Deal-aware source precedence (costable active deal else latest purchase/manual) sits UPSTREAM in
/// <see cref="PricingQueries.EffectiveCostablePricesAsync"/> and is pinned here through the
/// <c>SelectAsync</c> path plus the dedicated PricingQueries tests; this suite focuses on the selection
/// and convert/skip math that is unique to the rollup.
/// </summary>
public sealed class EffectivePriceRollupTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ParentId = Guid.CreateVersion7();
    private static readonly Guid V1 = Guid.CreateVersion7();
    private static readonly Guid V2 = Guid.CreateVersion7();
    private static readonly Guid GramId = Guid.CreateVersion7();
    private static readonly Guid EachId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 8, 1);

    private static PriceObservation Obs(Guid productId, decimal price, decimal quantity, Guid unitId, decimal? unitPrice = null) =>
        PriceObservation.Record(Household, productId, null, price, quantity, unitId, unitPrice,
            PriceSource.Purchase, "Superstore", SourceRef, DateTimeOffset.UtcNow, UserId);

    private static PriceRollupProduct Parent(params PriceRollupVariant[] variants) =>
        new(ParentId, GramId, IsParent: true, variants);

    private static PriceRollupVariant Live(Guid id, Guid defaultUnit) => new(id, defaultUnit);

    /// <summary>Convert delegate with a fake per-unit factor map: <c>factors[(from,to)]</c>, else a
    /// failure (no path) — enough to drive the convert/skip branches without a real unit graph.</summary>
    private static Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> Converter(
        params ((Guid From, Guid To) Key, decimal Factor)[] factors)
    {
        var map = factors.ToDictionary(f => f.Key, f => f.Factor);
        return (productId, amount, from, to, ct) =>
            map.TryGetValue((from, to), out var factor)
                ? Task.FromResult(Result<decimal>.Success(amount * factor))
                : Task.FromResult(Result<decimal>.Failure(Error.Custom("Catalog.UnresolvableConversion", "no path")));
    }

    private static async Task<(EffectivePriceCandidate? Best, int ConvertCalls)> SelectAsync(
        PriceRollupProduct product,
        IReadOnlyDictionary<Guid, PriceObservation> observations,
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> convert)
    {
        var calls = 0;
        Func<Guid, decimal, Guid, Guid, CancellationToken, Task<Result<decimal>>> counting =
            async (id, amount, from, to, ct) =>
            {
                calls++;
                return await convert(id, amount, from, to, ct);
            };
        var best = await EffectivePriceRollup.SelectFromObservationsAsync(product, observations, counting, CancellationToken.None);
        return (best, calls);
    }

    // ── Rule 2/4: live variant selection ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Parent with one priced live variant (same unit) — selects it with its provenance")]
    public async Task Parent_OnePricedLiveVariant_SelectsIt()
    {
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 1.80m, 100m, GramId),
        };

        var (best, calls) = await SelectAsync(Parent(Live(V1, GramId)), observations, Converter());

        Assert.NotNull(best);
        Assert.Equal(V1, best.ConcreteProductId);   // provenance = the variant
        Assert.Equal(ParentId, best.RequestedProductId);
        Assert.Equal(1.80m, best.Observation.Price);    // raw observation unchanged
        Assert.Equal(100m, best.ConvertedQuantity);
        Assert.Equal(0.018m, best.ConvertedUnitPrice); // 1.80 / 100
        // Same-unit: identity, no conversion round-trip.
        Assert.Equal(0, calls);
    }

    [Fact(DisplayName = "Parent with two live variants in different units — cheapest AFTER conversion wins")]
    public async Task Parent_TwoVariants_CheapestConvertedWins()
    {
        // V1: 1.80 / 100g (g) → 0.018/g. V2: 2.00 / 1 each, 1 each = 250 g → 0.008/g. V2 wins.
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 1.80m, 100m, GramId),
            [V2] = Obs(V2, 2.00m, 1m, EachId),
        };
        var converter = Converter(((EachId, GramId), 250m));

        var (best, _) = await SelectAsync(Parent(Live(V1, GramId), Live(V2, EachId)), observations, converter);

        Assert.NotNull(best);
        Assert.Equal(V2, best.ConcreteProductId);
        Assert.Equal(250m, best.ConvertedQuantity);
        Assert.Equal(0.008m, best.ConvertedUnitPrice);
        Assert.Equal(GramId, best.RequestedUnitId);
    }

    [Fact(DisplayName = "Parent with a cheaper but unconvertible variant — that variant is skipped, the convertible one wins")]
    public async Task Parent_CheaperUnconvertibleVariant_Skipped()
    {
        // V1 is cheaper raw but its each-unit observation cannot convert to gram (no path) → skipped.
        // V2 (g) wins even though its raw unit price is higher.
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 0.50m, 1m, EachId),
            [V2] = Obs(V2, 1.80m, 100m, GramId),
        };
        var converter = Converter(); // no conversion path at all

        var (best, _) = await SelectAsync(Parent(Live(V1, EachId), Live(V2, GramId)), observations, converter);

        Assert.NotNull(best);
        Assert.Equal(V2, best.ConcreteProductId);
    }

    [Fact(DisplayName = "Parent with only a parent observation (variants unpriced) — unpriced")]
    public async Task Parent_ParentOnlyObservation_Unpriced()
    {
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [ParentId] = Obs(ParentId, 1.80m, 100m, GramId), // orphaned parent row — not a ref
        };

        var (best, _) = await SelectAsync(Parent(Live(V1, GramId)), observations, Converter());

        Assert.Null(best);
    }

    [Fact(DisplayName = "Parent with no live variants — unpriced even with a parent observation")]
    public async Task Parent_NoLiveVariants_Unpriced()
    {
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [ParentId] = Obs(ParentId, 1.80m, 100m, GramId),
        };

        var (best, _) = await SelectAsync(Parent(), observations, Converter());

        Assert.Null(best);
    }

    [Fact(DisplayName = "Archived variant is excluded — a cheaper archived variant never wins")]
    public async Task Parent_ArchivedVariantExcluded()
    {
        // V1 (archived) is cheaper; V2 (live) is usable. Refs filter IsArchived, so V1 is never a candidate.
        var archived = new PriceRollupVariant(V1, GramId, IsArchived: true);
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 0.01m, 100m, GramId),
            [V2] = Obs(V2, 1.80m, 100m, GramId),
        };

        var (best, _) = await SelectAsync(Parent(Live(V2, GramId), archived), observations, Converter());

        Assert.NotNull(best);
        Assert.Equal(V2, best.ConcreteProductId);
    }

    // ── Rule 3/5: usable / convertible / unitless gates ──────────────────────────────────────────

    [Fact(DisplayName = "Variant with a unitless observation (empty unit) — skipped, parent unpriced")]
    public async Task Parent_UnitlessVariantObservation_Skipped()
    {
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 1.80m, 100m, Guid.Empty),
        };

        var (best, _) = await SelectAsync(Parent(Live(V1, GramId)), observations, Converter());

        Assert.Null(best);
    }

    [Fact(DisplayName = "Variant with a non-positive quantity observation — skipped, parent unpriced")]
    public async Task Parent_NonPositiveQuantity_Skipped()
    {
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [V1] = Obs(V1, 1.80m, 0m, GramId),
        };

        var (best, _) = await SelectAsync(Parent(Live(V1, GramId)), observations, Converter());

        Assert.Null(best);
    }

    // ── Rule 6: concrete leaf resolves to itself, no forced conversion ───────────────────────────

    [Fact(DisplayName = "Concrete leaf resolves to itself and keeps its own unit (no conversion round-trip)")]
    public async Task ConcreteLeaf_ResolvesToItself()
    {
        var leafId = Guid.CreateVersion7();
        var leaf = Obs(leafId, 2.50m, 200m, GramId);
        var observations = new Dictionary<Guid, PriceObservation> { [leafId] = leaf };
        var product = new PriceRollupProduct(leafId, GramId, IsParent: false, []);

        var (best, calls) = await SelectAsync(product, observations, Converter());

        Assert.NotNull(best);
        Assert.Equal(leafId, best.ConcreteProductId);
        Assert.Equal(leafId, best.RequestedProductId);
        Assert.Equal(200m, best.ConvertedQuantity);
        Assert.Equal(0.0125m, best.ConvertedUnitPrice); // 2.50 / 200
        Assert.Equal(0, calls); // leaf keeps observation unit — identity, no convert call
    }

    [Fact(DisplayName = "Concrete leaf with no usable observation — unpriced")]
    public async Task ConcreteLeaf_NoUsableObservation_Unpriced()
    {
        var leafId = Guid.CreateVersion7();
        var observations = new Dictionary<Guid, PriceObservation>
        {
            [leafId] = Obs(leafId, 2.50m, 0m, GramId), // zero quantity → unusable
        };
        var product = new PriceRollupProduct(leafId, GramId, IsParent: false, []);

        var (best, _) = await SelectAsync(product, observations, Converter());

        Assert.Null(best);
    }

    // ── SelectAsync: deal-aware source precedence through the shared projection ─────────────────

    [Fact(DisplayName = "SelectAsync: active costable deal beats later purchase on the winning variant")]
    public async Task SelectAsync_ActiveCostableDealWins()
    {
        var repo = new FakePriceObservationRepository();
        // On V1: older purchase vs an active costable deal (cheaper per unit → deal wins upstream).
        repo.Items.Add(DealOn(V1, 0.01m, Today.AddDays(-1), Today.AddDays(1)));
        repo.Items.Add(PurchaseOn(V1, 2.00m, Day(Today.AddDays(-2))));
        // On V2: only a purchase that is cheaper after conversion — must NOT win over V1's deal.
        repo.Items.Add(PurchaseOn(V2, 0.10m, Day(Today.AddDays(-1)))); // 0.10 each, 1 each = 1 g → 0.10/g, still > V1's 0.01/g
        var queries = new PricingQueries(repo);

        var best = await EffectivePriceRollup.SelectAsync(queries, Parent(Live(V1, GramId), Live(V2, EachId)), Today,
            Converter(((EachId, GramId), 1m)));

        Assert.NotNull(best);
        Assert.Equal(V1, best.ConcreteProductId);
    }

    [Fact(DisplayName = "SelectAsync: unitless deal never wins (no conversion basis) — purchase fallback on the variant")]
    public async Task SelectAsync_UnitlessDealFallsThroughToPurchase()
    {
        var repo = new FakePriceObservationRepository();
        var unitless = PriceObservation.Record(Household, V1, null, 0.01m, 1m, Guid.Empty, unitPrice: null,
            PriceSource.Deal, "Flyer", SourceRef, DateTimeOffset.UtcNow, UserId,
            validFrom: Today.AddDays(-1), validTo: Today.AddDays(1));
        repo.Items.Add(unitless);
        var purchase = PurchaseOn(V1, 2.00m, Day(Today.AddDays(-2)));
        repo.Items.Add(purchase);
        var queries = new PricingQueries(repo);

        var best = await EffectivePriceRollup.SelectAsync(queries, Parent(Live(V1, GramId)), Today, Converter());

        Assert.NotNull(best);
        Assert.Equal(purchase.Id.Value, best.Observation.Id.Value); // the purchase, not the unitless deal
    }

    // ── Helpers for the repo-backed path ─────────────────────────────────────────────────────────

    private static PriceObservation DealOn(Guid productId, decimal unitPrice, DateOnly from, DateOnly to) =>
        PriceObservation.Record(Household, productId, null, unitPrice, 1m, GramId, unitPrice,
            PriceSource.Deal, "Flyer", SourceRef, DateTimeOffset.UtcNow, UserId, validFrom: from, validTo: to);

    private static PriceObservation PurchaseOn(Guid productId, decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(Household, productId, null, unitPrice, 1m, GramId, unitPrice,
            PriceSource.Purchase, "Superstore", SourceRef, observedAt, UserId);

    private static DateTimeOffset Day(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);
}
