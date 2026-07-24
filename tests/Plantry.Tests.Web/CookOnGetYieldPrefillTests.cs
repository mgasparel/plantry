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
/// GET-side tests for the leftover prefill math and hint line on the Cook confirmation page
/// (plantry-iejb): <c>eatingTonight</c> drives <c>prefill = max(0, desiredServings - eatingTonight)</c>,
/// overriding the declared-yield <c>SuggestedQuantity</c> prefill, with a one-line hint. A direct
/// recipe-launched cook (no <c>eatingTonight</c>) keeps today's <c>SuggestedQuantity</c> prefill with no
/// hint, byte-for-byte. Also covers the leftovers block rendering unconditionally (dropping the old
/// <c>cook.Yield is { } y</c> gate) and the plan-provenance chip.
///
/// Assertions read the rendered HTML directly — the prefill value now lives in the yield block's
/// <c>x-data="cookYield(N)"</c> Alpine seed rather than a static <c>value=</c> attribute, since the
/// store field is Alpine-bound (<c>x-model.number</c>).
/// </summary>
public sealed class CookOnGetYieldPrefillTests : IDisposable
{
    private readonly CookYieldPrefillFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    private async Task<string> GetHtmlAsync(string query) =>
        await (await AuthenticatedClient().GetAsync($"{CookUrl}?{query}")).Content.ReadAsStringAsync();

    // ── Declared-yield recipe: eatingTonight overrides SuggestedQuantity, with a hint ─────────────

    [Fact]
    public async Task DeclaredYield_No_EatingTonight_Keeps_SuggestedQuantity_Prefill_With_No_Hint()
    {
        // Fixture is 4 servings, declared yield 4 (default) -> scale 1 -> SuggestedQuantity = 4.
        var html = await GetHtmlAsync("Servings=4");

        Assert.Contains("cookYield(4)", html);
        Assert.DoesNotContain("cook-yield-hint", html);
    }

    [Fact]
    public async Task DeclaredYield_EatingTonight_Overrides_SuggestedQuantity_And_Shows_Hint()
    {
        var html = await GetHtmlAsync("Servings=4&eatingTonight=2");

        // Declared SuggestedQuantity would be 4 (scale 1) -- the plan prefill (4-2=2) overrides it.
        Assert.Contains("cookYield(2)", html);
        Assert.Contains("cook-yield-hint", html);
        Assert.Contains("4 planned - 2 eating tonight", html);
    }

    [Fact]
    public async Task DeclaredYield_EatingTonight_Greater_Than_Servings_Prefills_Zero()
    {
        var html = await GetHtmlAsync("Servings=4&eatingTonight=6");

        Assert.Contains("cookYield(0)", html);
        Assert.Contains("4 planned - 6 eating tonight", html);
    }

    // ── Plan provenance chip ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlannedDishId_Present_Renders_Plan_Chip()
    {
        var html = await GetHtmlAsync($"Servings=4&plannedDishId={Guid.NewGuid()}");

        Assert.Contains("From your plan", html);
    }

    [Fact]
    public async Task PlannedDishId_Absent_Renders_No_Plan_Chip()
    {
        var html = await GetHtmlAsync("Servings=4");

        Assert.DoesNotContain("From your plan", html);
    }
}

// ── No-yield fixture recipe: the leftovers block always renders, prefill still applies ────────────

/// <summary>
/// Mirrors <see cref="CookOnGetYieldPrefillTests"/> against a recipe with NO declared yield
/// (<see cref="CookConfirmFixture.Build"/>) — the leftovers block renders unconditionally
/// (plantry-iejb drops the old <c>cook.Yield is { } y</c> gate) and the plan prefill still applies even
/// though there is no yield product yet.
/// </summary>
public sealed class CookOnGetNoYieldPrefillTests : IDisposable
{
    private readonly CookConfirmFragmentFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    private async Task<string> GetHtmlAsync(string query) =>
        await (await AuthenticatedClient().GetAsync($"{CookUrl}?{query}")).Content.ReadAsStringAsync();

    [Fact]
    public async Task NoYield_Block_Always_Renders_With_Zero_Default_And_No_Prompt()
    {
        var html = await GetHtmlAsync("Servings=4");

        Assert.Contains("cook-rail-yield", html);
        Assert.Contains("cookYield(0)", html);
        // The create/pick prompt markup is present (Alpine-gated by store > 0, x-cloak) but the block
        // itself renders unconditionally now.
        Assert.Contains("cook-yield-create", html);
    }

    [Fact]
    public async Task NoYield_EatingTonight_Still_Prefills_The_Store_Field()
    {
        var html = await GetHtmlAsync("Servings=4&eatingTonight=2");

        Assert.Contains("cookYield(2)", html);
        Assert.Contains("4 planned - 2 eating tonight", html);
    }
}

// ── WAF factory for the declared-yield GET prefill tests ──────────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the declared-yield leftover-prefill GET tests
/// (plantry-iejb). Mirrors <see cref="CookYieldPostFactory"/>'s seam replacements — GET-only, so the
/// inventory producer/consumer never actually run.
/// </summary>
internal sealed class CookYieldPrefillFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.BuildWithYield();
    public Guid RecipeId => Recipe.Id.Value;

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
                new FakeCookCatalogReader(CookConfirmFixture.ProductsWithYield(), CookConfirmFixture.UnitCodesWithYield()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookConfirmFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(new FakeCookInventoryConsumer());

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
