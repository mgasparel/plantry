using System.Net;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E smoke: asserts the /ready DB readiness probe returns 200 "Healthy" against the full
/// Aspire stack (live Postgres + web app). This is the behavioural proof required by the
/// acceptance criterion: "A readiness endpoint reports healthy when [the DB] is up."
///
/// The complementary unhealthy-state assertion lives in Plantry.Tests.Web
/// (ReadinessEndpointTests) where we can simulate a DB outage without Aspire.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class ReadinessEndpointTests(AppHostFixture appHost)
{
    [Fact(DisplayName = "/ready returns 200 Healthy when DB is up")]
    public async Task Ready_Returns_200_Healthy_When_DB_Is_Up()
    {
        using var http = new HttpClient { BaseAddress = new Uri(appHost.BaseUrl) };

        var response = await http.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", body.Trim());
    }
}
