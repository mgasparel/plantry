using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="StockUnitUnconvertibleDetector"/> (D1, tidy-up.md §3) over an in-memory
/// <see cref="StockFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the per-unit breakdown/summing shape and fingerprint-changes-on-fact-change direction that
/// the L3 tests in <c>StockDetectorsTests.cs</c> don't independently exercise.
/// </summary>
public sealed class StockUnitUnconvertibleDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid OnionId = Guid.NewGuid();
    private static readonly Guid EachId = Guid.NewGuid();
    private static readonly Guid PoundId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();

    private static UnitFact CountUnit(Guid id, string code) => new(id, code, code, "count", null, false);

    private static ProductFact Onion => new(OnionId, "Onion Yellow", true, EachId);

    private static StockFactsBag BagWithLots(params StockLotFact[] lots) => new(
        new Dictionary<Guid, StockProductFact> { [OnionId] = new(OnionId, null, lots) },
        new Dictionary<Guid, ProductFact> { [OnionId] = Onion },
        new Dictionary<Guid, UnitFact>
        {
            [EachId] = CountUnit(EachId, "ea"),
            [PoundId] = CountUnit(PoundId, "lb"),
            [GramId] = CountUnit(GramId, "g"),
        },
        new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

    /// <summary>Overload that also seeds <c>ConversionsByProduct</c> for the Onion product, so tests can
    /// exercise the detector's conversion-success suppression path (a lot unit that IS convertible to the
    /// product's default unit via an explicit <see cref="ConversionFact"/> — Count-dimension units connect
    /// only through an explicit conversion, never for free; see <c>UnitConverter</c>'s doc comment).</summary>
    private static StockFactsBag BagWithLots(StockLotFact[] lots, ConversionFact[] conversions) => new(
        new Dictionary<Guid, StockProductFact> { [OnionId] = new(OnionId, null, lots) },
        new Dictionary<Guid, ProductFact> { [OnionId] = Onion },
        new Dictionary<Guid, UnitFact>
        {
            [EachId] = CountUnit(EachId, "ea"),
            [PoundId] = CountUnit(PoundId, "lb"),
            [GramId] = CountUnit(GramId, "g"),
        },
        new Dictionary<Guid, IReadOnlyList<ConversionFact>> { [OnionId] = conversions });

    private static StockUnitUnconvertibleDetector BuildDetector(StockFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeStockFactsReadModel(bag), tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Lot unit unconvertible to display unit — produces a finding naming the product and its unconvertible unit")]
    public async Task UnconvertibleLot_ProducesFinding()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.StockUnitUnconvertible, finding.DetectorId);
        Assert.Equal(OnionId, finding.SubjectId);
        Assert.Equal("Onion Yellow", finding.SubjectName);
        Assert.Equal("3 lb in stock, display unit is ea", finding.Specifics);
        Assert.Equal($"/Catalog/Products/{OnionId}#conversions", finding.FixUrl);
    }

    [Fact(DisplayName = "Two distinct unconvertible units — specifics is a per-unit breakdown, terms ordered alphabetically by unit code")]
    public async Task TwoDistinctUnconvertibleUnits_ProducesPerUnitBreakdown()
    {
        var bag = BagWithLots(
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 200m, null, null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal("200 g + 3 lb in stock, display unit is ea", finding.Specifics);
    }

    [Fact(DisplayName = "Multiple lots of the same unconvertible unit — quantities sum into a single term")]
    public async Task SameUnconvertibleUnit_MultipleLots_SumIntoOneTerm()
    {
        var bag = BagWithLots(
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 2m, null, null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal("5 lb in stock, display unit is ea", finding.Specifics);
    }

    [Fact(DisplayName = "Lot already in the product's default unit — no finding")]
    public async Task LotInDefaultUnit_NoFinding()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, EachId, 3m, null, null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Lot in a different but convertible unit — no finding")]
    public async Task LotInConvertibleUnit_NoFinding()
    {
        var bag = BagWithLots(
            [new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true)],
            [new ConversionFact(OnionId, PoundId, EachId, 2m)]);

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var bag = BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true));

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: quantity change alone does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByQuantityChange()
    {
        var findingSmall = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 1m, null, null, true))).DetectAsync());
        var findingLarge = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 50m, null, null, true))).DetectAsync());

        Assert.Equal(findingSmall.FactsFingerprint, findingLarge.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a different unconvertible unit set changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentUnconvertibleUnit()
    {
        var findingLb = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true))).DetectAsync());
        var findingG = Assert.Single(
            await BuildDetector(BagWithLots(new StockLotFact(Guid.NewGuid(), OnionId, GramId, 3m, null, null, true))).DetectAsync());

        Assert.NotEqual(findingLb.FactsFingerprint, findingG.FactsFingerprint);
    }
}
