using Plantry.Composition.Infrastructure;
using Plantry.Web.Housekeeping;

namespace Plantry.Tests.Unit.Housekeeping.Detectors;

public sealed class MissingDefaultLocationDetectorTests
{
    private static readonly Guid HouseholdId = Guid.Parse("00000000-0000-0000-0000-000000000008");
    private static readonly Guid UnitId = Guid.Parse("00000000-0000-0000-0000-000000000009");

    private static MissingDefaultLocationDetector Build(params ProductFact[] products) =>
        new(
            new FakeStockFactsReadModel(new StockFactsBag(
                new Dictionary<Guid, StockProductFact>(),
                products.ToDictionary(p => p.ProductId),
                new Dictionary<Guid, UnitFact>(),
                new Dictionary<Guid, IReadOnlyList<ConversionFact>>())),
            new FakeTenantContext(HouseholdId));

    [Fact]
    public async Task EligibleCatalogOnlyProduct_ProducesExactFinding()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var finding = Assert.Single(await Build(new ProductFact(id, "Milk", true, UnitId)).DetectAsync());

        Assert.Equal(DetectorId.ProductMissingDefaultLocation, finding.DetectorId);
        Assert.Equal("Products without a default location", Build().GroupTitle);
        Assert.Equal("No product-specific home is set, so new stock entries have no location prefilled and the product can appear in Take Stock's “No location” flow.", Build().GroupConsequence);
        Assert.Equal("i-location", Build().IconName);
        Assert.Equal("Default location not set", finding.Specifics);
        Assert.Equal("New stock entries have no product-specific location prefilled; existing lots may still be stored in a physical location.", finding.Consequence);
        Assert.Equal($"/Catalog/Products/{id}", finding.FixUrl);
        Assert.Equal("Fix in Catalog", finding.FixLabel);
        Assert.Equal(Severity.Advisory, Build().Severity);
    }

    [Fact]
    public async Task DefaultSetUntrackedAndParent_AreExcluded()
    {
        var eligible = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000011"), "Eligible", true, UnitId);
        var withDefault = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000012"), "Default", true, UnitId, Guid.Parse("00000000-0000-0000-0000-000000000013"));
        var untracked = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000014"), "Untracked", false, UnitId);
        var parent = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000015"), "Parent", true, UnitId, null, true);

        var findings = await Build(eligible, withDefault, untracked, parent).DetectAsync();

        Assert.Equal(eligible.ProductId, Assert.Single(findings).SubjectId);
    }

    [Fact]
    public async Task FindingsSortOrdinalIgnoreCaseAndShareConstantFingerprint()
    {
        var z = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000016"), "zucchini", true, UnitId);
        var a = new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000017"), "Apple", true, UnitId);
        var findings = await Build(z, a).DetectAsync();

        Assert.Equal(["Apple", "zucchini"], findings.Select(x => x.SubjectName));
        Assert.Equal(findings[0].FactsFingerprint, findings[1].FactsFingerprint);
    }

    [Fact]
    public async Task PhysicalLotLocation_DoesNotChangeEligibilityOrFingerprint()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000019");
        var product = new ProductFact(id, "Beans", true, UnitId);
        var lot = new StockLotFact(Guid.Parse("00000000-0000-0000-0000-000000000020"), id, UnitId, 4m, null, null, true);
        var withLot = new StockFactsBag(
            new Dictionary<Guid, StockProductFact> { [id] = new(id, null, [lot]) },
            new Dictionary<Guid, ProductFact> { [id] = product },
            new Dictionary<Guid, UnitFact>(),
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());

        var withoutLotFinding = Assert.Single(await Build(product).DetectAsync());
        var withLotFinding = Assert.Single(await new MissingDefaultLocationDetector(
            new FakeStockFactsReadModel(withLot), new FakeTenantContext(HouseholdId)).DetectAsync());

        Assert.Equal(withoutLotFinding.FactsFingerprint, withLotFinding.FactsFingerprint);
        Assert.Equal("Default location not set", withLotFinding.Specifics);
    }

    [Fact]
    public async Task FingerprintIsExactVersionedSha256()
    {
        var finding = Assert.Single(await Build(new ProductFact(Guid.Parse("00000000-0000-0000-0000-000000000021"), "Rice", true, UnitId)).DetectAsync());
        Assert.Equal("16CC6ACBFCB30B4F83B4246ADC9A032A4B862087F8E4A6984C13E62BA5D7CB40", finding.FactsFingerprint);
    }

    [Fact]
    public async Task SettingDefaultLocation_MakesFindingDisappear()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000022");
        var missing = Assert.Single(await Build(new ProductFact(id, "Oil", true, UnitId)).DetectAsync());
        var fixedProduct = new ProductFact(id, "Oil", true, UnitId, Guid.Parse("00000000-0000-0000-0000-000000000023"));

        Assert.Empty(await Build(fixedProduct).DetectAsync());
        Assert.NotEqual(missing.SubjectId, Guid.Parse("00000000-0000-0000-0000-000000000024"));
    }

    [Fact]
    public async Task NoTenant_ReturnsEmpty()
    {
        var bag = new StockFactsBag(
            new Dictionary<Guid, StockProductFact>(),
            new Dictionary<Guid, ProductFact> { [Guid.Parse("00000000-0000-0000-0000-000000000018")] = new(Guid.Parse("00000000-0000-0000-0000-000000000018"), "Milk", true, UnitId) },
            new Dictionary<Guid, UnitFact>(),
            new Dictionary<Guid, IReadOnlyList<ConversionFact>>());
        var detector = new MissingDefaultLocationDetector(new FakeStockFactsReadModel(bag), new FakeTenantContext(null));

        Assert.Empty(await detector.DetectAsync());
    }
}
