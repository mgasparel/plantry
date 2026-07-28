using Plantry.Identity.Application;
using Plantry.Web.Inventory;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 test for <see cref="HouseholdExpiryDefaultsReaderAdapter"/> — the composition-root ACL adapter
/// that lets Catalog's <c>ExpiryDefaultResolver</c> freeze/thaw fallback read Identity's household
/// defaults without Catalog depending on Identity directly (plantry-hh1f). A thin delegate, so the only
/// thing worth proving is that it forwards the exact tuple <see cref="IHouseholdExpiryDefaults"/> returns.
/// </summary>
public sealed class HouseholdExpiryDefaultsReaderAdapterTests
{
    [Fact(DisplayName = "GetDefaultsAsync forwards the (AfterFreezing, AfterThawing) tuple from IHouseholdExpiryDefaults verbatim")]
    public async Task Forwards_Defaults_Verbatim()
    {
        var adapter = new HouseholdExpiryDefaultsReaderAdapter(new FakeHouseholdExpiryDefaults(45, 5));

        var result = await adapter.GetDefaultsAsync();

        Assert.Equal((45, 5), result);
    }

    private sealed class FakeHouseholdExpiryDefaults(int afterFreezing, int afterThawing) : IHouseholdExpiryDefaults
    {
        public Task<(int AfterFreezing, int AfterThawing)> GetAsync(CancellationToken ct = default) =>
            Task.FromResult((afterFreezing, afterThawing));
    }
}
