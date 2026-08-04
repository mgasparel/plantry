using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.Pantry.Application;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="UnitCodesAccessor"/> (plantry-47tc, plantry-hw39 code review) — the
/// per-request cache over <see cref="IUnitRepository"/>'s unit codes that mirrors
/// <see cref="HouseholdExpiryDefaultsAccessor"/>. Proves the caching contract itself at the unit
/// level (call-counting fake, no database); the L3
/// <c>UnitCodesAccessorQueryCountTests</c> pins the same contract against a real Postgres
/// <c>units</c> table via <c>QueryCountingInterceptor</c>.
/// <para>
/// <c>Plantry.Pantry.Domain.Unit</c> is aliased to <see cref="CatalogUnit"/> — this file's own
/// namespace, <c>Plantry.Tests.Unit.Composition</c>, has an enclosing segment literally named
/// <c>Unit</c> (<c>Plantry.Tests.Unit</c>), which shadows the unqualified domain type name.
/// </para>
/// </summary>
public sealed class UnitCodesAccessorTests
{
    [Fact(DisplayName = "GetCodesAsync resolves the underlying repository once and caches the result for subsequent calls on the same instance")]
    public async Task Caches_Within_Same_Instance()
    {
        var household = HouseholdId.New();
        var grams = CatalogUnit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kilograms = CatalogUnit.Create(household, "kg", "kilograms", Dimension.Mass, 1000m);
        var repo = new CountingUnitRepository(grams, kilograms);
        var accessor = new UnitCodesAccessor(repo);

        var first = await accessor.GetCodesAsync();
        var second = await accessor.GetCodesAsync();
        var third = await accessor.GetCodesAsync();

        Assert.Equal("g", first[grams.Id.Value]);
        Assert.Equal("kg", first[kilograms.Id.Value]);
        Assert.Equal("g", second[grams.Id.Value]);
        Assert.Equal("g", third[grams.Id.Value]);
        Assert.Equal(1, repo.ListCallCount);
    }

    [Fact(DisplayName = "A new accessor instance re-resolves — the cache is per-instance, not static/shared")]
    public async Task Does_Not_Cache_Across_Instances()
    {
        var household = HouseholdId.New();
        var grams = CatalogUnit.Create(household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kilograms = CatalogUnit.Create(household, "kg", "kilograms", Dimension.Mass, 1000m);
        var repo = new CountingUnitRepository(grams, kilograms);

        var accessor1 = new UnitCodesAccessor(repo);
        await accessor1.GetCodesAsync();
        Assert.Equal(1, repo.ListCallCount);

        var accessor2 = new UnitCodesAccessor(repo);
        await accessor2.GetCodesAsync();
        Assert.Equal(2, repo.ListCallCount);
    }

    private sealed class CountingUnitRepository(params CatalogUnit[] units) : IUnitRepository
    {
        public int ListCallCount { get; private set; }

        public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default)
        {
            ListCallCount++;
            return Task.FromResult(units.ToList());
        }

        public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AddAsync(CatalogUnit unit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
