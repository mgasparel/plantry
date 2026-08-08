using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.Web.Pricing;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="UnitPriceCalculatorAdapter"/>'s per-request unit memoization (plantry-bb7p
/// code review) — the receipt-review page normalizes every line's own price in a loop, so repeated
/// <c>TryNormalizeAsync</c> calls for the same unit must resolve <see cref="IUnitRepository.FindAsync"/>
/// exactly once per unit id per (scoped) adapter instance. Mirrors the
/// <see cref="UnitCodesAccessorTests"/> call-counting pattern for the same symptom
/// (plantry-47tc/plantry-hw39).
/// <para><c>Plantry.Pantry.Domain.Unit</c> is aliased to <see cref="CatalogUnit"/> — this file's own
/// namespace has an enclosing segment literally named <c>Unit</c> (<c>Plantry.Tests.Unit</c>), which
/// shadows the unqualified domain type name.</para>
/// </summary>
public sealed class UnitPriceCalculatorAdapterTests
{
    [Fact(DisplayName = "Two TryNormalizeAsync calls for the same unit id issue exactly one FindAsync")]
    public async Task Memoizes_Repeat_Lookups_For_Same_Unit()
    {
        var household = HouseholdId.New();
        var grams = CatalogUnit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var repo = new CountingUnitRepository(grams);
        var adapter = new UnitPriceCalculatorAdapter(repo);

        var first = await adapter.TryNormalizeAsync(price: 5m, quantity: 500m, grams.Id.Value);
        var second = await adapter.TryNormalizeAsync(price: 3m, quantity: 100m, grams.Id.Value);

        Assert.Equal(0.01m, first);
        Assert.Equal(0.03m, second);
        Assert.Equal(1, repo.FindCallCount);
    }

    [Fact(DisplayName = "Two different unit ids issue two FindAsync calls — the cache is per unit id, not global")]
    public async Task Resolves_Each_Distinct_Unit_Once()
    {
        var household = HouseholdId.New();
        var grams = CatalogUnit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kilograms = CatalogUnit.Create(household, "kg", "kilograms", Dimension.Mass, 1000m);
        var repo = new CountingUnitRepository(grams, kilograms);
        var adapter = new UnitPriceCalculatorAdapter(repo);

        await adapter.TryNormalizeAsync(price: 5m, quantity: 500m, grams.Id.Value);
        await adapter.TryNormalizeAsync(price: 12m, quantity: 2m, kilograms.Id.Value);

        Assert.Equal(2, repo.FindCallCount);
    }

    [Fact(DisplayName = "A missing unit's null result is memoized too — the soft-fail does not re-query per line")]
    public async Task Memoizes_Negative_Lookups()
    {
        var repo = new CountingUnitRepository();
        var adapter = new UnitPriceCalculatorAdapter(repo);
        var unknownId = Guid.CreateVersion7();

        var first = await adapter.TryNormalizeAsync(price: 5m, quantity: 1m, unknownId);
        var second = await adapter.TryNormalizeAsync(price: 7m, quantity: 2m, unknownId);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, repo.FindCallCount);
    }

    private sealed class CountingUnitRepository(params CatalogUnit[] units) : IUnitRepository
    {
        public int FindCallCount { get; private set; }

        public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default)
        {
            FindCallCount++;
            return Task.FromResult(units.FirstOrDefault(u => u.Id == id));
        }

        public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AddAsync(CatalogUnit unit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
