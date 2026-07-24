using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Handler-level tests for the CookEvent plan-provenance plumbing (plantry-wskj): the Cook page
/// accepts a <c>plannedDishId</c> GET query param, round-trips it through the POST via a hidden
/// field, and CookRecipe stamps it onto the minted CookEvent. On a plan-launched cook the page
/// PRG-redirects to the meal plan with a save toast instead of the Detail page; a direct
/// (non-plan) cook keeps today's Detail-page redirect byte-for-byte.
///
/// Assertions capture at the <see cref="RecordingFakeCookEventRepository"/> seam over a real
/// <c>CookRecipe</c> — no ICookRecipe interface is introduced (same pattern as
/// <see cref="CookOnPostResolutionTests"/>/<see cref="CookOnPostYieldTests"/>).
/// </summary>
public sealed class CookOnPostPlannedDishTests : IDisposable
{
    private readonly CookPlannedDishPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // The fixture recipe is 4 servings; POSTing Servings=4 means scale=1.
    private const int PostedServings = 4;

    private static readonly Guid PlannedDishId = Guid.Parse("dddddddd-0000-0000-0000-000000000009");

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    /// <summary>
    /// GETs the Cook page (optionally with a <c>plannedDishId</c> query param) and returns the
    /// rendered HTML alongside the harvested antiforgery token — used both to assert the GET-side
    /// hidden-field round-trip and to authenticate the following POST.
    /// </summary>
    private async Task<(string Html, string Token)> GetCookPageAsync(HttpClient client, Guid? plannedDishId)
    {
        var url = $"{CookUrl}?Servings={PostedServings}";
        if (plannedDishId is { } id)
            url += $"&plannedDishId={id}";

        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Cook page.");
        return (html, match.Groups[1].Value);
    }

    private async Task<HttpResponseMessage> PostCookAsync(
        HttpClient client, string token, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var allFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Id", _factory.RecipeId.ToString()),
            new("Servings", PostedServings.ToString()),
        };
        allFields.AddRange(fields);
        return await client.PostAsync(CookUrl, new FormUrlEncodedContent(allFields));
    }

    [Fact]
    public async Task Get_with_plannedDishId_renders_hidden_field_carrying_it()
    {
        var client = AuthenticatedClient();

        var (html, _) = await GetCookPageAsync(client, PlannedDishId);

        Assert.Contains(
            $"<input type=\"hidden\" name=\"PlannedDishId\" value=\"{PlannedDishId}\" />", html);
    }

    [Fact]
    public async Task Get_without_plannedDishId_renders_no_hidden_field()
    {
        var client = AuthenticatedClient();

        var (html, _) = await GetCookPageAsync(client, plannedDishId: null);

        Assert.DoesNotContain("name=\"PlannedDishId\"", html);
    }

    [Fact]
    public async Task Post_with_plannedDishId_stamps_it_on_the_minted_cook_event()
    {
        var client = AuthenticatedClient();
        var (_, token) = await GetCookPageAsync(client, PlannedDishId);

        var response = await PostCookAsync(client, token,
        [
            new("PlannedDishId", PlannedDishId.ToString()),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var cookEvent = Assert.Single(_factory.EventRepo.Added);
        Assert.Equal(PlannedDishId, cookEvent.PlannedDishId);
    }

    [Fact]
    public async Task Post_without_plannedDishId_leaves_it_null_on_the_minted_cook_event()
    {
        var client = AuthenticatedClient();
        var (_, token) = await GetCookPageAsync(client, plannedDishId: null);

        var response = await PostCookAsync(client, token, []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var cookEvent = Assert.Single(_factory.EventRepo.Added);
        Assert.Null(cookEvent.PlannedDishId);
    }

    [Fact]
    public async Task Post_with_plannedDishId_redirects_to_meal_plan_with_a_toast()
    {
        var client = AuthenticatedClient();
        var (_, token) = await GetCookPageAsync(client, PlannedDishId);

        var response = await PostCookAsync(client, token,
        [
            new("PlannedDishId", PlannedDishId.ToString()),
        ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/MealPlan", response.Headers.Location?.ToString());

        // TempData["ToastMessage"] was set before the redirect (plantry-u7n9 pattern) — the default
        // CookieTempDataProvider carries it via a Set-Cookie on this very response.
        Assert.Contains(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.Contains("CookieTempDataProvider"));
    }

    [Fact]
    public async Task Post_without_plannedDishId_keeps_the_direct_recipe_detail_redirect()
    {
        var client = AuthenticatedClient();
        var (_, token) = await GetCookPageAsync(client, plannedDishId: null);

        var response = await PostCookAsync(client, token, []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Recipes/{_factory.RecipeId}", response.Headers.Location?.ToString());
    }
}

// ── WAF factory for the plan-provenance OnPostAsync tests ────────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the Cook POST plan-provenance tests
/// (plantry-wskj). Mirrors <see cref="CookPostFactory"/>'s seam replacements but swaps in a
/// <see cref="RecordingFakeCookEventRepository"/> so tests can inspect the minted CookEvent's
/// <see cref="CookEvent.PlannedDishId"/>.
/// </summary>
internal sealed class CookPlannedDishPostFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    public RecordingFakeCookEventRepository EventRepo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookConfirmFixture.Products(), CookConfirmFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookConfirmFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new RecordingFakeCookInventoryConsumer());

            // The seam under test: record the minted CookEvent instead of hitting the database.
            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(EventRepo);

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
