using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Application;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// DB-free Catalog Detail test seam for the Identity-owned household freeze/thaw defaults.
/// Detail-page tests otherwise do not need Identity persistence, so they use the aggregate's
/// documented 90/3 defaults while exercising the product-local policy modes.
/// </summary>
public static class FakeHouseholdExpiryDefaultsRegistration
{
    public static IServiceCollection AddFakeHouseholdExpiryDefaults(
        this IServiceCollection services, int afterFreezing = 90, int afterThawing = 3)
    {
        services.RemoveAll<IHouseholdExpiryDefaultsReader>();
        services.AddSingleton<IHouseholdExpiryDefaultsReader>(
            new FakeHouseholdExpiryDefaultsReader(afterFreezing, afterThawing));
        return services;
    }
}

internal sealed class FakeHouseholdExpiryDefaultsReader(int afterFreezing, int afterThawing)
    : IHouseholdExpiryDefaultsReader
{
    public Task<(int AfterFreezing, int AfterThawing)> GetDefaultsAsync(CancellationToken ct = default) =>
        Task.FromResult((afterFreezing, afterThawing));
}
