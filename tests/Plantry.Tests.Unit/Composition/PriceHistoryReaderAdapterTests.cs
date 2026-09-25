using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Market;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="PriceHistoryReaderAdapter"/> and <see cref="PriceRollupContextBuilder"/>
/// (plantry-oh27.5) — the Composition-level helper that resolves the parent/variant rollup context via
/// <see cref="ICatalogProductReader"/> so a future consumer (the parent detail page, the deals-review
/// purchase context) never builds a <see cref="PriceRollupProduct"/> by hand, mirroring
/// <c>PriceReaderAdapter</c>/<c>MealPlanPriceReaderAdapter</c>'s use of the same port for
/// <see cref="EffectivePriceRollup"/>.
/// </summary>
public sealed class PriceHistoryReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid GramId = Guid.CreateVersion7();
    private static readonly Guid EachId = Guid.CreateVersion7();

    // unitPrice is always populated here even where the rollup path being exercised doesn't need it (the
    // leaf path reads PricingQueries.PriceHistoryAsync, which filters to observations with a usable
    // precomputed UnitPrice — see its doc comment).
    private static PriceObservation Purchase(Guid productId, decimal price, decimal quantity, Guid unitId, DateTimeOffset observedAt) =>
        PriceObservation.Record(Household, productId, null, price, quantity, unitId, unitPrice: price / quantity,
            PriceSource.Purchase, "Store", null, observedAt, UserId);

    private static PriceHistoryReaderAdapter Adapter(
        Plantry.Tests.Unit.Market.FakePriceObservationRepository repo, Plantry.Tests.Unit.Recipes.Application.FakeCatalogProductReader catalog,
        Plantry.Tests.Unit.Recipes.Application.FakeUnitConverter converter) =>
        new(new PricingQueries(repo), catalog, converter);

    [Fact(DisplayName = "A product absent from the catalog resolves as a leaf, ReferenceUnitId is Guid.Empty")]
    public async Task CatalogAbsentId_ResolvesAsLeaf()
    {
        var leafId = Guid.CreateVersion7();
        var repo = new Plantry.Tests.Unit.Market.FakePriceObservationRepository();
        repo.Items.Add(Purchase(leafId, 2.00m, 1m, EachId, DateTimeOffset.UtcNow));
        var catalog = new Plantry.Tests.Unit.Recipes.Application.FakeCatalogProductReader();
        var converter = new Plantry.Tests.Unit.Recipes.Application.FakeUnitConverter();

        var result = await Adapter(repo, catalog, converter).ForProductAsync(leafId);

        Assert.Equal(leafId, result.RequestedProductId);
        Assert.Equal(Guid.Empty, result.ReferenceUnitId);
        Assert.Single(result.Points);
        Assert.Equal([leafId], result.ContributingProductIds);
    }

    [Fact(DisplayName = "A parent with a per-variant unit override converts that variant's history into the parent's default unit")]
    public async Task ParentId_ConvertsVariantHistory_IntoParentDefaultUnit()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var repo = new Plantry.Tests.Unit.Market.FakePriceObservationRepository();
        repo.Items.Add(Purchase(variantId, 2.00m, 1m, EachId, DateTimeOffset.UtcNow)); // 1 each = 250 g -> 0.008/g
        var catalog = new Plantry.Tests.Unit.Recipes.Application.FakeCatalogProductReader();
        catalog.Register(new CatalogProduct(parentId, "Bubly", TrackStock: false, GramId, null, IsParent: true,
            [variantId], new Dictionary<Guid, Guid> { [variantId] = EachId }));
        var converter = new Plantry.Tests.Unit.Recipes.Application.FakeUnitConverter();
        converter.AddPath(variantId, EachId, GramId, 250m);

        var result = await Adapter(repo, catalog, converter).ForProductAsync(parentId);

        Assert.Equal(GramId, result.ReferenceUnitId);
        var point = Assert.Single(result.Points);
        Assert.Equal(0.008m, point.UnitPrice);
        Assert.Equal([variantId], result.ContributingProductIds);
    }

    [Fact(DisplayName = "A parent variant whose per-variant unit is unset falls back to the parent's default unit (no conversion needed)")]
    public async Task ParentId_VariantWithoutUnitOverride_FallsBackToParentDefaultUnit()
    {
        var parentId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var repo = new Plantry.Tests.Unit.Market.FakePriceObservationRepository();
        repo.Items.Add(Purchase(variantId, 1.80m, 100m, GramId, DateTimeOffset.UtcNow));
        var catalog = new Plantry.Tests.Unit.Recipes.Application.FakeCatalogProductReader();
        // No VariantDefaultUnitIds entry for variantId — PriceRollupContextBuilder falls back to the
        // parent's DefaultUnitId (GramId), which happens to already match the observation's own unit.
        catalog.Register(new CatalogProduct(parentId, "Bubly", TrackStock: false, GramId, null, IsParent: true, [variantId]));
        var converter = new Plantry.Tests.Unit.Recipes.Application.FakeUnitConverter();

        var result = await Adapter(repo, catalog, converter).ForProductAsync(parentId);

        var point = Assert.Single(result.Points);
        Assert.Equal(0.018m, point.UnitPrice);
    }
}
