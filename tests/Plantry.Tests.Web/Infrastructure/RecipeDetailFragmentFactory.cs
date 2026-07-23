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

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// No-op <see cref="IShoppingListWriter"/> for the recipe Detail L4 snapshot tests.
/// The Detail page GET handler never calls the shopping writer — only the POST AddMissing
/// handler does. This fake satisfies the DI container so <see cref="AddMissingToShoppingList"/>
/// can be resolved for the GET path without a real Shopping database connection.
/// </summary>
file sealed class NullShoppingListWriter : IShoppingListWriter
{
    public static readonly NullShoppingListWriter Instance = new();
    public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
        IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default)
        => Task.FromResult(ShoppingSyncOutcome.None);
}

/// <summary>
/// Empty <see cref="IShoppingListRepository"/> for the recipe Detail L4 snapshot tests.
/// The Detail GET handler consults <c>ShoppingListQueryService.HasRecipeContributionAsync</c> to
/// decide the add-to-list buttons' greyed state (plantry-yt0m); returning no list keeps the buttons
/// in their default enabled state (recipe not yet on the list) so the existing snapshots hold, and
/// avoids a real Shopping database connection.
/// </summary>
file sealed class NullShoppingListRepository : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default)
        => Task.FromResult<ShoppingList?>(null);

    public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// L4 WebApplicationFactory for the recipe Detail page. Boots the real <c>Plantry.Web</c> pipeline
/// (routing, authorization, Razor rendering) but replaces all Postgres-backed seams the Detail
/// page depends on — the recipe repository, the tag repository, the catalog product reader, the
/// inventory stock reader, the price reader, and the unit converter — with in-memory fakes, and
/// swaps cookie auth for a header-driven test scheme. No database is touched; rendered HTML is deterministic.
///
/// <para>Default scenario (used by the base snapshot tests): mixed fulfillment status —
/// Pasta InStock, Tomatoes Low, Garlic Missing, Salt Untracked; Partial cost (Garlic un-priced).</para>
///
/// <para>Derived factories override <see cref="Prices"/> to exercise the other cost-completeness
/// render paths: <see cref="RecipeDetailFullCostFactory"/> (Full) and
/// <see cref="RecipeDetailNoCostFactory"/> (None).</para>
/// </summary>
public class RecipeDetailFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>The recipe used in all Detail snapshots; expose it so tests can construct the URL.</summary>
    public Recipe Recipe { get; }

    public RecipeDetailFragmentFactory()
    {
        Recipe = BuildRecipe();
    }

    /// <summary>
    /// The recipe fixture this factory serves. Default is the mixed-shape fixture
    /// (<see cref="RecipeDetailFixture.Build"/>). A derived factory overrides this to exercise a
    /// different recipe shape — e.g. <see cref="RecipeDetailAllUntrackedFactory"/>, which serves the
    /// all-untracked shape (<see cref="RecipeDetailFixture.BuildAllUntracked"/>).
    /// </summary>
    protected virtual Recipe BuildRecipe() => RecipeDetailFixture.Build();

    public Guid RecipeId => Recipe.Id.Value;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Price points the Detail page costs against. Default is Partial (Garlic un-priced).
    /// Derived factories override this to exercise the Full and None cost-completeness render paths.
    /// </summary>
    protected virtual IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.Prices();

    /// <summary>
    /// Household display currency the Detail page's cost meta renders with (plantry-2x6e.2). Default USD so the
    /// base snapshots keep their "$" values; a derived factory overrides it to exercise the non-USD symbol path.
    /// </summary>
    protected virtual string DisplayCurrency => "USD";

    /// <summary>
    /// Stock snapshots the Detail page's fulfillment reads. Default is the mixed-status scenario
    /// (Pasta InStock, Tomatoes Low, Garlic Missing). A derived factory overrides this to exercise the
    /// unit-gap render path (plantry-z2sr) where on-hand stock can't be converted to the recipe unit.
    /// </summary>
    protected virtual IReadOnlyDictionary<Guid, ProductStock> Stock => RecipeDetailFixture.Stock(Today);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Non-Development: skips startup migrations/seeding and the Dev-pages gate.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Household display currency (plantry-2x6e.2): a deterministic fake so the cost meta resolves without
            // a real Identity DB (the page GET now reads IDisplayCurrency).
            services.AddFakeDisplayCurrency(DisplayCurrency);
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme mirrors ReviewFragmentFactory.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: returns the fixture recipe for the owning household.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            // Tag repository: resolves the fixture's known tag id → name mapping.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeDetailFixture.TagNames()));

            // Catalog product reader: returns the fixture product set + unit codes.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCatalogProductReader(RecipeDetailFixture.Products(), RecipeDetailFixture.UnitCodes()));

            // Inventory stock reader: mixed statuses (Pasta=InStock, Tomatoes=Low, Garlic=Missing).
            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeDetailStockReader(Stock));

            // Price reader: scenario-dependent (see Prices). Default = Partial (Garlic un-priced).
            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(Prices));

            // Unit converter: identity (ingredient unit == product default unit in fixture).
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
            services.AddFakeQuantityFormatter();

            // Shopping list writer: no-op for GET-path tests (AddMissing is POST-only).
            // Satisfies the AddMissingToShoppingList DI constructor without a real Shopping DB.
            services.RemoveAll<IShoppingListWriter>();
            services.AddSingleton<IShoppingListWriter>(NullShoppingListWriter.Instance);

            // Shopping list repository: empty (no list) so the Detail GET path's
            // HasRecipeContributionAsync check (plantry-yt0m) resolves to false without a real
            // Shopping DB — the add-to-list buttons render in their default enabled state.
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository, NullShoppingListRepository>();
        });
    }
}

/// <summary>
/// Variant: every costable ingredient priced → <c>CostCompleteness.Full</c>. The meta strip shows a
/// mono cost value with no "~" partial-estimate marker.
/// </summary>
public sealed class RecipeDetailFullCostFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesFull();
}

/// <summary>
/// Variant: no ingredient priced → <c>CostCompleteness.None</c>. The meta strip renders the dash cell
/// (no <c>rd-meta__val--mono</c> value, no total).
/// </summary>
public sealed class RecipeDetailNoCostFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesNone();
}

/// <summary>
/// Variant: fully-priced (Full) cost rendered for a EUR household (plantry-2x6e.2) — proves the cost meta
/// renders the '€' symbol from MoneyDisplay rather than a hardcoded '$'.
/// </summary>
public sealed class RecipeDetailEurCostFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesFull();
    protected override string DisplayCurrency => "EUR";
}

/// <summary>
/// Variant: every ingredient is untracked / "to taste" (null Quantity/UnitId) — <c>CostableCount == 0</c>
/// (plantry-7vb7). Costs to <c>CostCompleteness.None</c> like <see cref="RecipeDetailNoCostFactory"/>, but
/// with an empty <c>MissingPriceProductIds</c> list (nothing is costable, so nothing can be "missing a
/// price") — the meta strip must render the bare dash with no "i" trigger/popover at all.
/// </summary>
public sealed class RecipeDetailAllUntrackedFactory : RecipeDetailFragmentFactory
{
    protected override Recipe BuildRecipe() => RecipeDetailFixture.BuildAllUntracked();
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesNone();
}

/// <summary>
/// Variant: Garlic on hand as a weight (grams) while the recipe line is a count ("ea") with no conversion
/// path — the unit-gap render path (plantry-z2sr). The Garlic row must read "Can't compare units" with the
/// info-tone status and the explanatory popover, not the flat danger "Not in your pantry".
/// </summary>
public sealed class RecipeDetailUnitGapFactory : RecipeDetailFragmentFactory
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    protected override IReadOnlyDictionary<Guid, ProductStock> Stock => RecipeDetailFixture.StockWithUnitGap(Today);
}
