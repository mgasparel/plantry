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
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render assertions for the expired-badge feature (plantry-17n).
/// Verifies that the shared <c>_IngredientRows.cshtml</c> partial renders:
/// <list type="bullet">
///   <item>The red <c>rd-ing-soon--expired</c> pill with "Expired Nd ago" text when
///   <c>ExpiresWithinDays</c> is negative (lot has passed its use-by date).</item>
///   <item>The amber <c>rd-ing-soon</c> pill with "N d" text when positive (expiring soon).</item>
/// </list>
/// Both the initial full-page render and the servings-stepper OOB swap (via
/// <c>OnGetFulfilmentAsync</c>) use the same shared partial, so the assertions cover both paths.
/// </summary>
public sealed class RecipeDetailExpiredBadgeTests(
    RecipeDetailExpiredLotFactory expiredFactory,
    RecipeDetailExpiringSoonFactory soonFactory)
    : IClassFixture<RecipeDetailExpiredLotFactory>,
      IClassFixture<RecipeDetailExpiringSoonFactory>
{
    // ── Initial full-page render: expired pill ────────────────────────────────

    /// <summary>
    /// Full-page GET: garlic lot expired 3 days ago → rendered as red
    /// <c>rd-ing-soon--expired</c> pill with text "Expired 3d ago".
    /// </summary>
    [Fact]
    public async Task Initial_Render_Shows_Expired_Pill_For_Expired_Lot()
    {
        var html = await GetPageHtmlAsync(expiredFactory);

        Assert.Contains("rd-ing-soon--expired", html, StringComparison.Ordinal);
        Assert.Contains("Expired 3d ago",        html, StringComparison.Ordinal);
        // The amber (not-yet-expired) pill must NOT appear for this scenario.
        Assert.DoesNotContain("rd-ing-soon--expired rd-ing-soon", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Full-page GET: cook flow is NOT blocked for a recipe with an expired ingredient.
    /// The "Cook this" button must be present (no hard gating on negative ExpiresWithinDays).
    /// </summary>
    [Fact]
    public async Task Initial_Render_Cook_Button_Present_For_Expired_Ingredient()
    {
        var html = await GetPageHtmlAsync(expiredFactory);

        // The Cook link is always rendered — expired stock does not remove or disable it.
        Assert.Contains("btn--cook", html, StringComparison.Ordinal);
        Assert.Contains("/Cook?Servings=", html, StringComparison.Ordinal);
    }

    // ── Initial full-page render: soon (amber) pill ───────────────────────────

    /// <summary>
    /// Full-page GET: garlic lot expires in 2 days → rendered as amber <c>rd-ing-soon</c>
    /// pill (not the <c>--expired</c> variant).
    /// </summary>
    [Fact]
    public async Task Initial_Render_Shows_Soon_Pill_For_Non_Expired_Lot()
    {
        var html = await GetPageHtmlAsync(soonFactory);

        Assert.Contains("rd-ing-soon", html, StringComparison.Ordinal);
        Assert.Contains("2 d",          html, StringComparison.Ordinal);
        // Must NOT render the red expired pill.
        Assert.DoesNotContain("rd-ing-soon--expired", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Expired",              html, StringComparison.Ordinal);
    }

    // ── OOB swap (htmx partial): expired pill ────────────────────────────────

    /// <summary>
    /// OOB swap via <c>OnGetFulfilmentAsync</c>: the fulfillment partial (which emits
    /// <c>#rd-ing-rows</c> via <c>_DetailsFulfilmentCard.cshtml</c>) must also render
    /// the red expired pill, proving the shared partial covers both paths.
    /// </summary>
    [Fact]
    public async Task OOB_Swap_Shows_Expired_Pill_For_Expired_Lot()
    {
        var client = expiredFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(
            $"/Recipes/{expiredFactory.RecipeId}?handler=Fulfilment&servings=4");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // OOB block must be present in the htmx response.
        Assert.Contains("hx-swap-oob=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"rd-ing-rows\"",   html, StringComparison.Ordinal);

        // The expired pill must appear in the OOB ingredient rows block.
        Assert.Contains("rd-ing-soon--expired", html, StringComparison.Ordinal);
        Assert.Contains("Expired 3d ago",        html, StringComparison.Ordinal);
    }

    /// <summary>
    /// OOB swap: expiring-soon (amber) pill appears and expired pill does not,
    /// symmetric with the initial-render soon test.
    /// </summary>
    [Fact]
    public async Task OOB_Swap_Shows_Soon_Pill_For_Non_Expired_Lot()
    {
        var client = soonFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(
            $"/Recipes/{soonFactory.RecipeId}?handler=Fulfilment&servings=4");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("rd-ing-soon",            html, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-ing-soon--expired", html, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> GetPageHtmlAsync(RecipeDetailExpiredBadgeFactoryBase factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

// ── Shared base factory ───────────────────────────────────────────────────────

/// <summary>
/// Base WebApplicationFactory for the expired-badge L4 tests. Wires up the full
/// <c>Plantry.Web</c> pipeline with in-memory fakes, identical to
/// <see cref="RecipeDetailFragmentFactory"/> but parameterised on the stock scenario.
/// </summary>
public abstract class RecipeDetailExpiredBadgeFactoryBase : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = RecipeDetailFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    protected static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    protected abstract IReadOnlyDictionary<Guid, ProductStock> BuildStock(DateOnly today);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency();
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

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeDetailFixture.TagNames()));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCatalogProductReader(RecipeDetailFixture.Products(), RecipeDetailFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeDetailStockReader(BuildStock(Today)));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeDetailPriceReader(RecipeDetailFixture.Prices()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IShoppingListWriter>();
            services.AddSingleton<IShoppingListWriter>(NullShoppingListWriterForExpiredBadge.Instance);

            // Empty shopping list so the Detail GET path's HasRecipeContributionAsync check
            // (plantry-yt0m) resolves to false without a real Shopping DB.
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository, NullShoppingListRepositoryForExpiredBadge>();
        });
    }
}

/// <summary>
/// Factory for the expired-lot scenario: garlic expired 3 days ago
/// (SoonestExpiry = today - 3 → ExpiresWithinDays = -3 → red pill).
/// </summary>
public sealed class RecipeDetailExpiredLotFactory : RecipeDetailExpiredBadgeFactoryBase
{
    protected override IReadOnlyDictionary<Guid, ProductStock> BuildStock(DateOnly today) =>
        RecipeDetailFixture.StockWithExpiredLot(today);
}

/// <summary>
/// Factory for the expiring-soon scenario: garlic expires in 2 days
/// (SoonestExpiry = today + 2 → ExpiresWithinDays = +2 → amber pill).
/// </summary>
public sealed class RecipeDetailExpiringSoonFactory : RecipeDetailExpiredBadgeFactoryBase
{
    protected override IReadOnlyDictionary<Guid, ProductStock> BuildStock(DateOnly today) =>
        RecipeDetailFixture.StockWithExpiry(today);
}

/// <summary>No-op shopping list writer (file-scoped to avoid naming collision).</summary>
file sealed class NullShoppingListWriterForExpiredBadge : IShoppingListWriter
{
    public static readonly NullShoppingListWriterForExpiredBadge Instance = new();

    public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default)
        => Task.FromResult(ShoppingSyncOutcome.None);
}

/// <summary>Empty shopping list repository (file-scoped) — the Detail GET path treats the recipe as
/// not yet on the list (plantry-yt0m), avoiding a real Shopping DB connection.</summary>
file sealed class NullShoppingListRepositoryForExpiredBadge : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
