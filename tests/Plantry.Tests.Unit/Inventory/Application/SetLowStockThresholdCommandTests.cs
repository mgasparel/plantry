using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

public sealed class SetLowStockThresholdCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();

    private FakeCatalogReadFacade CatalogWith(bool canHoldStock = true)
    {
        var catalog = new FakeCatalogReadFacade();
        catalog.Products.Add(new CatalogProductInfo(_productId, "Flour", "Baking", _unitId, "g", canHoldStock));
        return catalog;
    }

    private SetLowStockThresholdCommand Command(
        FakeLowStockRuleRepository rules, FakeCatalogReadFacade catalog, Guid? household,
        decimal? threshold = 5m, IClock? clock = null) =>
        new(_productId, threshold, rules, catalog, clock ?? Clock, new FakeTenantContext(household));

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var rules = new FakeLowStockRuleRepository();

        var result = await Command(rules, CatalogWith(), household: null).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(rules.Items);
    }

    [Fact]
    public async Task Fails_When_Product_Does_Not_Exist()
    {
        var rules = new FakeLowStockRuleRepository();

        var result = await Command(rules, new FakeCatalogReadFacade(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.UnknownProduct", result.Error.Code);
        Assert.Empty(rules.Items);
    }

    [Fact(DisplayName = "Succeeds for a parent product — the CanHoldStock rejection is dropped "
        + "(plantry-oh27.1); UI enablement for parents lands in a later bead")]
    public async Task Succeeds_For_A_Parent_Product()
    {
        var rules = new FakeLowStockRuleRepository();

        var result = await Command(rules, CatalogWith(canHoldStock: false), _household, threshold: 3m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var rule = Assert.Single(rules.Items);
        Assert.Equal(3m, rule.Threshold);
        Assert.Equal(_productId, rule.ProductId);
    }

    [Fact]
    public async Task Creates_A_New_Rule_And_Persists()
    {
        var rules = new FakeLowStockRuleRepository();

        var result = await Command(rules, CatalogWith(), _household, threshold: 7m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var rule = Assert.Single(rules.Items);
        Assert.Equal(7m, rule.Threshold);
        Assert.Equal(HouseholdId.From(_household), rule.HouseholdId);
        Assert.Equal(_productId, rule.ProductId);
        Assert.Equal(1, rules.SaveChangesCalls);
    }

    [Fact]
    public async Task Updates_An_Existing_Rules_Threshold_And_Persists()
    {
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock));

        var result = await Command(rules, CatalogWith(), _household, threshold: 3m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var rule = Assert.Single(rules.Items);
        Assert.Equal(3m, rule.Threshold);
    }

    [Fact(DisplayName = "A null threshold removes the rule — 'no threshold' is the absence of a row")]
    public async Task Removes_The_Rule_When_Set_To_Null()
    {
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock));

        var result = await Command(rules, CatalogWith(), _household, threshold: null).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(rules.Items);
    }

    [Fact(DisplayName = "A zero (or negative) threshold also removes the rule, matching the old "
        + "null-or-zero-means-no-threshold behaviour")]
    public async Task Removes_The_Rule_When_Set_To_Zero()
    {
        var rules = new FakeLowStockRuleRepository();
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, Clock));

        var result = await Command(rules, CatalogWith(), _household, threshold: 0m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(rules.Items);
    }

    [Fact(DisplayName = "Clearing a threshold that was never set is a no-op success, not an error")]
    public async Task Clearing_A_Nonexistent_Rule_Is_A_NoOp_Success()
    {
        var rules = new FakeLowStockRuleRepository();

        var result = await Command(rules, CatalogWith(), _household, threshold: null).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(rules.Items);
        Assert.Equal(0, rules.SaveChangesCalls);
    }

    [Fact]
    public async Task UpdatedAt_Is_Bumped_After_Successful_Update()
    {
        var clock = new MutableClock();
        var rules = new FakeLowStockRuleRepository();
        var rule = LowStockRule.Create(HouseholdId.From(_household), _productId, 5m, clock);
        var beforeSet = rule.UpdatedAt;
        rules.Items.Add(rule);

        clock.Advance(TimeSpan.FromHours(1));
        var result = await Command(rules, CatalogWith(), _household, threshold: 4m, clock: clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var updated = Assert.Single(rules.Items);
        Assert.Equal(clock.UtcNow, updated.UpdatedAt);
        Assert.True(updated.UpdatedAt > beforeSet);
    }

    [Fact(DisplayName = "A concurrent create race (another request wins the insert) reloads the "
        + "winning row and applies this threshold instead of throwing")]
    public async Task Create_Reloads_And_Applies_Value_When_The_Insert_Race_Is_Lost()
    {
        var rules = new FakeLowStockRuleRepository();
        // Simulate another request winning the insert: the repository reports failure on the first
        // TryAddAndSaveAsync attempt, but a row for this product now exists (as the real EF repository's
        // caller would find on reload) — the fake seeds it directly since it has no database to race against.
        rules.FailNextTryAdd = true;
        rules.Items.Add(LowStockRule.Create(HouseholdId.From(_household), _productId, 1m, Clock));

        var result = await Command(rules, CatalogWith(), _household, threshold: 6m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var rule = Assert.Single(rules.Items);
        Assert.Equal(6m, rule.Threshold);
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }
}
