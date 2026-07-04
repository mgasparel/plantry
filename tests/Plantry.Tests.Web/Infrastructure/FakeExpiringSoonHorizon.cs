using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Inventory.Application;
using Plantry.Recipes.Application;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Test-host registration for the per-household "expiring soon" horizon (plantry-5yhd). The real
/// <c>IExpiringSoonHorizon</c>/<c>IExpiringSoonHorizonReader</c> read the setting from Inventory's
/// database; L4 factories that render the Today, Recipe, or Meal-Plan pages with faked Inventory
/// ports have no database, so they must stub the horizon too. Returns the Inventory default (7),
/// preserving the behaviour those pages had before the setting existed.
/// </summary>
public static class FakeExpiringSoonHorizonRegistration
{
    public static IServiceCollection AddFakeExpiringSoonHorizon(this IServiceCollection services, int days = 7)
    {
        services.RemoveAll<IExpiringSoonHorizon>();
        services.AddSingleton<IExpiringSoonHorizon>(new FakeHorizon(days));
        services.RemoveAll<IExpiringSoonHorizonReader>();
        services.AddSingleton<IExpiringSoonHorizonReader>(new FakeHorizonReader(days));
        return services;
    }

    private sealed class FakeHorizon(int days) : IExpiringSoonHorizon
    {
        public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(days);
    }

    private sealed class FakeHorizonReader(int days) : IExpiringSoonHorizonReader
    {
        public Task<int> GetDaysAsync(CancellationToken ct = default) => Task.FromResult(days);
    }
}
