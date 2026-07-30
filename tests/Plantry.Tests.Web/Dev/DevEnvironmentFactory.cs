using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Boots Plantry.Web in the <b>Development</b> environment so /Dev pages are mapped and render (e.g.
/// /Dev/Endpoints, /Dev — the component gallery). Every consumer reads only in-memory/static data, so
/// no database is required; a placeholder, unreachable connection string keeps DI construction happy.
/// Shared by <see cref="DevEndpointsPageTests"/> and <see cref="DevGalleryPageTests"/> (plantry-4gft) —
/// do not fork a second copy; extend this one if a future /Dev test needs different configuration (as
/// <c>DevSweepQueueingTests.QueueRecordingDevFactory</c> does, to additionally replace the background
/// task queue).
/// </summary>
internal class DevEnvironmentFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:plantrydb"] =
                    "Host=127.0.0.1;Port=9;Database=plantrydb;Username=app_user;Password=x;Timeout=1;CommandTimeout=1",
                ["DataProtection:KeyPath"] = Path.GetTempPath(),
            });
        });
    }
}
