using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Intake;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 fragment tests for the Upload page's Recent-intakes panel entry points into history (receipt-
/// intake-history.md H12): the panel header's "View all" link, and a committed row's title becoming a
/// link to its session detail.
/// </summary>
public sealed class UploadHistoryLinksTests : IClassFixture<UploadHistoryLinksFragmentFactory>
{
    private readonly UploadHistoryLinksFragmentFactory _factory;

    public UploadHistoryLinksTests(UploadHistoryLinksFragmentFactory factory) => _factory = factory;

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, IntakeHistoryFixture.HouseholdAId.ToString());
        return client;
    }

    [Fact]
    public async Task Panel_header_has_a_view_all_link_to_history()
    {
        var resp = await AuthClient().GetAsync("/Intake/Upload");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains("View all", html);
        Assert.Contains("href=\"/Intake/History\"", html);
    }

    [Fact]
    public async Task Committed_row_title_links_to_its_session_detail()
    {
        var resp = await AuthClient().GetAsync("/Intake/Upload");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains($"/Intake/Session/{_factory.Committed.Id.Value}", html);
        Assert.Contains("Costco Wholesale", html);
    }

    [Fact]
    public async Task Ready_row_resume_link_uses_route_param_not_query_string()
    {
        // Regression for plantry-z3s4: Review.cshtml declares @page "{id:guid}", so the Resume link
        // must place the session id in the route segment (/Intake/Review/{id}), not a query string
        // (/Intake/Review?id=...) — the latter 404s because it doesn't match the route template.
        var resp = await AuthClient().GetAsync("/Intake/Upload");
        var html = await resp.Content.ReadAsStringAsync();

        Assert.Contains($"/Intake/Review/{_factory.Ready.Id.Value}", html);
        Assert.DoesNotContain("/Intake/Review?id=", html);
    }
}

/// <summary>
/// L4 WebApplicationFactory dedicated to the Upload page's Recent-intakes panel, seeded with a real
/// Committed session so <see cref="GetRecentSessionsQuery"/> (via
/// <see cref="MultiSessionImportSessionRepository.ListRecentAsync"/>) has something to render — mirrors
/// <c>UploadFragmentFactory</c>'s wiring but swaps in the multi-session repository.
/// </summary>
public sealed class UploadHistoryLinksFragmentFactory : WebApplicationFactory<Program>
{
    public ImportSession Committed { get; } = IntakeHistoryFixture.BuildCommitted(
        IntakeHistoryFixture.HouseholdAId, Guid.CreateVersion7(), Guid.CreateVersion7());

    public ImportSession Ready { get; } = IntakeHistoryFixture.BuildReady(IntakeHistoryFixture.HouseholdAId);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency();
            services.AddFakeExpiringSoonHorizon();

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IImportSessionRepository>();
            services.AddScoped<IImportSessionRepository>(
                sp => new MultiSessionImportSessionRepository(sp.GetRequiredService<ITenantContext>(), Committed, Ready));

            services.RemoveAll<ICatalogHintProvider>();
            services.AddScoped<ICatalogHintProvider>(_ => new EmptyCatalogHintProvider());

            services.RemoveAll<InventoryQueryService>();
            services.AddScoped<InventoryQueryService>(_ => new StubInventoryQueryService(inStock: 0, expiringSoon: 0));
        });
    }
}

internal sealed class EmptyCatalogHintProvider : ICatalogHintProvider
{
    public Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductHint>>([]);
}
