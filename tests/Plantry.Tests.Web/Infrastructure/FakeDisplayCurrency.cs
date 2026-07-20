using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Application;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Test-host registration for the per-household display currency (plantry-2x6e.1). The real
/// <see cref="DisplayCurrencyService"/> resolves the code from Identity's database via
/// <c>IHouseholdRepository</c>; L4 factories that render budget-write pages (/MealPlan,
/// /Settings/MealPlanning) with faked ports have no database, so they must stub it too. Returns the
/// USD default, preserving the behaviour those pages had before the setting existed.
/// </summary>
public static class FakeDisplayCurrencyRegistration
{
    public static IServiceCollection AddFakeDisplayCurrency(this IServiceCollection services, string currency = "USD")
    {
        services.RemoveAll<IDisplayCurrency>();
        services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency(currency));
        return services;
    }

    private sealed class FakeDisplayCurrency(string currency) : IDisplayCurrency
    {
        public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult(currency);
    }
}
