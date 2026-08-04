using Plantry.Pantry.Application;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Test double for <see cref="IHouseholdExpiryDefaultsReader"/> (plantry-hh1f) — a fixed
/// (after-freezing, after-thawing) pair, defaulting to the same 90/3 the <c>Household</c> aggregate and
/// its EF column defaults carry, for the many <see cref="Plantry.Web.Inventory.CatalogReadFacade"/>
/// construction sites across this project that are not exercising freeze/thaw behaviour.
/// </summary>
public sealed class FakeHouseholdExpiryDefaultsReader(int afterFreezing = 90, int afterThawing = 3)
    : IHouseholdExpiryDefaultsReader
{
    public Task<(int AfterFreezing, int AfterThawing)> GetDefaultsAsync(CancellationToken ct = default) =>
        Task.FromResult((afterFreezing, afterThawing));
}
