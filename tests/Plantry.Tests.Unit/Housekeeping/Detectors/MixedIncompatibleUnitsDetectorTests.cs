using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

/// <summary>
/// L1 unit tests for <see cref="MixedIncompatibleUnitsDetector"/> (D6, tidy-up.md §3) over an in-memory
/// <see cref="StockFactsBag"/> — restores the fast coverage the retired fake-port test file provided,
/// including the "both convert" no-finding case, the "no active lots" no-finding case, and the
/// fingerprint-changes-with-a-different-unit-set direction the L3 tests don't independently exercise.
/// </summary>
public sealed class MixedIncompatibleUnitsDetectorTests
{
    private static readonly Guid HouseholdGuid = Guid.NewGuid();
    private static readonly Guid OnionId = Guid.NewGuid();
    private static readonly Guid EachId = Guid.NewGuid();
    private static readonly Guid PoundId = Guid.NewGuid();
    private static readonly Guid GramId = Guid.NewGuid();

    private static UnitFact CountUnit(Guid id, string code) => new(id, code, code, "count", null, false);

    private static ProductFact Onion => new(OnionId, "Onion Yellow", true, EachId);

    private static StockFactsBag BagWithLots(
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>>? conversions, params StockLotFact[] lots) => new(
        new Dictionary<Guid, StockProductFact> { [OnionId] = new(OnionId, null, lots) },
        new Dictionary<Guid, ProductFact> { [OnionId] = Onion },
        new Dictionary<Guid, UnitFact>
        {
            [EachId] = CountUnit(EachId, "ea"),
            [PoundId] = CountUnit(PoundId, "lb"),
            [GramId] = CountUnit(GramId, "g"),
        },
        conversions ?? new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

    private static MixedIncompatibleUnitsDetector BuildDetector(StockFactsBag bag, ITenantContext? tenant = null) =>
        new(new FakeStockFactsReadModel(bag), tenant ?? new FakeTenantContext(HouseholdGuid));

    [Fact(DisplayName = "Two units, neither convertible to the display unit — fires (falls back to \"?\")")]
    public async Task TwoIncompatibleUnits_ProducesFinding()
    {
        var bag = BagWithLots(
            null,
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 2m, null, null, true));

        var finding = Assert.Single(await BuildDetector(bag).DetectAsync());

        Assert.Equal(DetectorId.StockMixedIncompatibleUnits, finding.DetectorId);
        Assert.Equal(OnionId, finding.SubjectId);
    }

    [Fact(DisplayName = "Two units that both convert to the display unit — no finding")]
    public async Task TwoConvertibleUnits_NoFinding()
    {
        var conversions = new Dictionary<Guid, IReadOnlyList<ConversionFact>>
        {
            [OnionId] =
            [
                new ConversionFact(OnionId, PoundId, EachId, 1m),
                new ConversionFact(OnionId, GramId, EachId, 1m),
            ],
        };
        var bag = BagWithLots(
            conversions,
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 2m, null, null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "Single unit, unconvertible to display — falls back to the lot's own unit, not \"?\" — no finding")]
    public async Task SingleUnconvertibleUnit_NoFinding()
    {
        var bag = BagWithLots(null, new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true));

        Assert.Empty(await BuildDetector(bag).DetectAsync()); // D1's case, not D6's
    }

    [Fact(DisplayName = "No active lots — no finding")]
    public async Task NoActiveLots_NoFinding()
    {
        var bag = BagWithLots(null);

        Assert.Empty(await BuildDetector(bag).DetectAsync());
    }

    [Fact(DisplayName = "No tenant — returns no findings")]
    public async Task NoTenant_ReturnsEmpty()
    {
        var bag = BagWithLots(
            null,
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 2m, null, null, true));

        Assert.Empty(await BuildDetector(bag, new FakeTenantContext(null)).DetectAsync());
    }

    [Fact(DisplayName = "Fingerprint pinning: quantity change alone does NOT change the fingerprint")]
    public async Task Fingerprint_UnaffectedByQuantityChange()
    {
        var findingSmall = Assert.Single(await BuildDetector(BagWithLots(
            null,
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 1m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 1m, null, null, true))).DetectAsync());

        var findingLarge = Assert.Single(await BuildDetector(BagWithLots(
            null,
            new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 50m, null, null, true),
            new StockLotFact(Guid.NewGuid(), OnionId, GramId, 80m, null, null, true))).DetectAsync());

        Assert.Equal(findingSmall.FactsFingerprint, findingLarge.FactsFingerprint);
    }

    [Fact(DisplayName = "Fingerprint pinning: a different unit set changes the fingerprint")]
    public async Task Fingerprint_ChangesWithDifferentUnitSet()
    {
        var ounceId = Guid.NewGuid();
        var bagA = new StockFactsBag(
            new Dictionary<Guid, StockProductFact>
            {
                [OnionId] = new(OnionId, null,
                [
                    new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
                    new StockLotFact(Guid.NewGuid(), OnionId, GramId, 2m, null, null, true),
                ]),
            },
            new Dictionary<Guid, ProductFact> { [OnionId] = Onion },
            new Dictionary<Guid, UnitFact>
            {
                [EachId] = CountUnit(EachId, "ea"),
                [PoundId] = CountUnit(PoundId, "lb"),
                [GramId] = CountUnit(GramId, "g"),
                [ounceId] = CountUnit(ounceId, "oz"),
            },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());
        var findingA = Assert.Single(await BuildDetector(bagA).DetectAsync());

        var bagB = new StockFactsBag(
            new Dictionary<Guid, StockProductFact>
            {
                [OnionId] = new(OnionId, null,
                [
                    new StockLotFact(Guid.NewGuid(), OnionId, PoundId, 3m, null, null, true),
                    new StockLotFact(Guid.NewGuid(), OnionId, ounceId, 2m, null, null, true),
                ]),
            },
            new Dictionary<Guid, ProductFact> { [OnionId] = Onion },
            new Dictionary<Guid, UnitFact>
            {
                [EachId] = CountUnit(EachId, "ea"),
                [PoundId] = CountUnit(PoundId, "lb"),
                [GramId] = CountUnit(GramId, "g"),
                [ounceId] = CountUnit(ounceId, "oz"),
            },
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());
        var findingB = Assert.Single(await BuildDetector(bagB).DetectAsync());

        Assert.NotEqual(findingA.FactsFingerprint, findingB.FactsFingerprint);
    }
}
