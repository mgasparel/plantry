using Plantry.Identity.Application;
using Plantry.Web.Inventory;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="HouseholdExpiryDefaultsAccessor"/> (plantry-hw39, absorbing plantry-rsy1) —
/// the per-request cache over <see cref="IHouseholdExpiryDefaults"/> that mirrors
/// <c>DisplayCurrencyAccessor</c>. Proves the caching contract itself at the unit level (call-counting
/// fake, no database); the L3 <c>HouseholdExpiryDefaultsAccessorQueryCountTests</c> pins the same
/// contract against a real Postgres <c>households</c> table via <c>QueryCountingInterceptor</c>.
/// </summary>
public sealed class HouseholdExpiryDefaultsAccessorTests
{
    [Fact(DisplayName = "GetAsync resolves the underlying source once and caches the result for subsequent calls on the same instance")]
    public async Task Caches_Within_Same_Instance()
    {
        var source = new CountingHouseholdExpiryDefaults(45, 5);
        var accessor = new HouseholdExpiryDefaultsAccessor(source);

        var first = await accessor.GetAsync();
        var second = await accessor.GetAsync();
        var third = await accessor.GetAsync();

        Assert.Equal((45, 5), first);
        Assert.Equal((45, 5), second);
        Assert.Equal((45, 5), third);
        Assert.Equal(1, source.CallCount);
    }

    [Fact(DisplayName = "A new accessor instance re-resolves — the cache is per-instance, not static/shared")]
    public async Task Does_Not_Cache_Across_Instances()
    {
        var source = new CountingHouseholdExpiryDefaults(45, 5);

        var accessor1 = new HouseholdExpiryDefaultsAccessor(source);
        await accessor1.GetAsync();
        Assert.Equal(1, source.CallCount);

        var accessor2 = new HouseholdExpiryDefaultsAccessor(source);
        await accessor2.GetAsync();
        Assert.Equal(2, source.CallCount);
    }

    private sealed class CountingHouseholdExpiryDefaults(int afterFreezing, int afterThawing) : IHouseholdExpiryDefaults
    {
        public int CallCount { get; private set; }

        public Task<(int AfterFreezing, int AfterThawing)> GetAsync(CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult((afterFreezing, afterThawing));
        }
    }
}
