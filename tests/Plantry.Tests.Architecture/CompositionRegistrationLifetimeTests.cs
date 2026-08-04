using Microsoft.Extensions.DependencyInjection;
using Plantry.Composition;
using Plantry.Planning.Application;
using Xunit;

namespace Plantry.Tests.Architecture;

/// <summary>
/// Pins the DI lifetime of every cross-context ACL adapter registered by
/// <see cref="CompositionServiceCollectionExtensions.AddCrossContextAdapters"/> to
/// <see cref="ServiceLifetime.Scoped"/> (plantry-b6sc, follow-up from plantry-jefp's Opus critic DEFER).
/// <para>
/// This is not just a perf convention: <c>MealPlanCatalogProductReaderAdapter</c>'s memoised
/// <c>_unitCodes</c> cache (added in plantry-jefp) is only tenant-safe because the adapter is
/// registered Scoped over a scoped <c>CatalogDbContext</c>, combined with RLS pinning
/// <c>app.household_id</c> per connection for the scope's lifetime. Every other ACL adapter here
/// captures its own scoped <c>DbContext</c>, so the same captive-dependency / cross-household leak
/// risk applies uniformly. If a future "simplify DI" refactor silently widened any of these
/// registrations to Singleton, a captured DbContext (or a memoised per-household value, as
/// plantry-jefp's field already does) could leak between households across requests — and nothing
/// but this test would catch it.
/// </para>
/// </summary>
public sealed class CompositionRegistrationLifetimeTests
{
    [Fact]
    public void AddCrossContextAdapters_Registers_Every_Adapter_As_Scoped()
    {
        var services = new ServiceCollection();

        services.AddCrossContextAdapters();

        var nonScoped = services
            .Where(d => d.Lifetime != ServiceLifetime.Scoped)
            .Select(d => $"{d.ServiceType.FullName} -> {d.Lifetime}")
            .ToList();

        Assert.True(nonScoped.Count == 0,
            "One or more Plantry.Composition ACL adapters registered by AddCrossContextAdapters are " +
            "not Scoped. Every adapter here captures a scoped DbContext (or, for " +
            "IMealPlanCatalogProductReader specifically, a per-household memoised cache added in " +
            "plantry-jefp) — widening any of these to Singleton/Transient risks a captive dependency " +
            "or a cross-household data leak that RLS's per-scope connection pinning would no longer " +
            "protect against:\n" + string.Join("\n", nonScoped));

        // Anchor: the load-bearing registration plantry-jefp's memoised _unitCodes cache depends on must
        // still be registered HERE and Scoped. Single() fails if it was removed (e.g. moved into the host
        // as AddSingleton by a "simplify DI" refactor) or double-registered — the all-Scoped sweep above
        // cannot catch a registration that has vanished from this method.
        var mealPlanReader = Assert.Single(
            services, d => d.ServiceType == typeof(IMealPlanCatalogProductReader));
        Assert.Equal(ServiceLifetime.Scoped, mealPlanReader.Lifetime);
    }
}
