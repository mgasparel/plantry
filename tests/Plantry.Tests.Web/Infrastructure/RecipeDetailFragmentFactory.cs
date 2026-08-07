using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Planning.Domain;

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
///
/// <para>Rating fixtures (plantry-zlwp.3): <see cref="Ratings"/> and <see cref="Members"/> default to
/// empty (no ratings, single-member household) so the base snapshots exercise the "no ratings
/// anywhere: just the input with 'Tap to rate' hint" path. <see cref="RecipeDetailRatedMultiMemberFactory"/>
/// exercises the household-summary-line render path.</para>
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
    /// Existing <see cref="RecipeRating"/> rows for <see cref="Recipe"/> (plantry-zlwp.3). Default empty —
    /// no one has rated. A derived factory populates this to exercise the household summary line / popover.
    /// </summary>
    protected virtual IReadOnlyList<RecipeRating> Ratings => [];

    /// <summary>
    /// Household member directory (plantry-zlwp.3) backing <see cref="IHouseholdMemberReader"/> — display
    /// names/initials for the rating popover. Default empty (a single-member household with no directory
    /// entries still renders correctly: MyStars falls back to 0, no household line). A derived factory
    /// populates this to exercise the multi-member household summary line.
    /// </summary>
    protected virtual IReadOnlyList<HouseholdMember> Members => [];

    /// <summary>
    /// The recipe fixture this factory serves. Default is the mixed-shape fixture
    /// (<see cref="RecipeDetailFixture.Build"/>). A derived factory overrides this to exercise a
    /// different recipe shape — e.g. <see cref="RecipeDetailAllUntrackedFactory"/>, which serves the
    /// all-untracked shape (<see cref="RecipeDetailFixture.BuildAllUntracked"/>).
    /// </summary>
    protected virtual Recipe BuildRecipe() => RecipeDetailFixture.Build();

    public Guid RecipeId => Recipe.Id.Value;

    // Fixed instant, not a live clock read (plantry-3orq arbiter FIX-IN-CASE — the same unpinned-clock
    // seam plantry-4tb4 opened on the Recipe Details page's "today" derivation, closed here by mirroring
    // the pin RecipeDetailExpiredBadgeTests.cs already ships). Pinning the SUT's IClock (below) to this
    // exact instance keeps the fixture's Today and the SUT's clock.ToLocalDate(clock.UtcNow) in agreement.
    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
    protected static readonly DateOnly Today = Clock.ToLocalDate(Clock.UtcNow);

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

    /// <summary>
    /// Catalog products the Detail page resolves ingredient (and, since plantry-aqpa.5, substitute)
    /// names from. Default is the fixture's four-product set. A derived factory overrides this to add
    /// a substitute product that is never itself an ingredient line (e.g.
    /// <see cref="RecipeDetailViaSubstituteFactory"/>).
    /// </summary>
    protected virtual IReadOnlyDictionary<Guid, CatalogProduct> Products => RecipeDetailFixture.Products();

    /// <summary>
    /// Product ids the catalog reader reports as home-produced (<c>Product.IsProduced</c>, plantry-4osq).
    /// Default empty — no base scenario exercises the produced-exclusion path. A derived factory
    /// (<see cref="RecipeDetailProducedExclusionFactory"/>) overrides this to mark a specific product.
    /// </summary>
    protected virtual IReadOnlySet<Guid> ProducedProductIds => new HashSet<Guid>();

    /// <summary>
    /// Substitution edges (plantry-aqpa.1/.2/.5) the Detail page's fulfillment computation and
    /// substitute-name touchpoint read. Default is empty — no fixture scenario exercises substitution
    /// edges by default. A derived factory overrides this to exercise the "in stock via substitute"
    /// display touchpoint (<see cref="RecipeDetailViaSubstituteFactory"/>).
    /// </summary>
    protected virtual ISubstitutionReader SubstitutionReader => new FakeDetailSubstitutionReader();

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

            // Pin the host clock to the same fixed instant Today is derived from, so the SUT's
            // clock.ToLocalDate(clock.UtcNow) and this fixture's Today always agree (see comment on
            // the Today field above).
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);

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

            // Catalog product reader: returns the fixture product set + unit codes (+ the produced-id
            // set, plantry-4osq — empty by default, see ProducedProductIds).
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCatalogProductReader(Products, RecipeDetailFixture.UnitCodes(), produced: ProducedProductIds));

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

            // Substitution reader (plantry-aqpa.1/.2/.5): default empty. Without this override
            // FulfillmentService resolves the real Postgres-backed SubstitutionReader, which this
            // factory's no-database setup cannot satisfy.
            services.RemoveAll<ISubstitutionReader>();
            services.AddSingleton(SubstitutionReader);

            // Shopping list writer: no-op for GET-path tests (AddMissing is POST-only).
            // Satisfies the AddMissingToShoppingList DI constructor without a real Shopping DB.
            services.RemoveAll<IShoppingListWriter>();
            services.AddSingleton<IShoppingListWriter>(NullShoppingListWriter.Instance);

            // Shopping list repository: empty (no list) so the Detail GET path's
            // HasRecipeContributionAsync check (plantry-yt0m) resolves to false without a real
            // Shopping DB — the add-to-list buttons render in their default enabled state.
            services.RemoveAll<IShoppingListRepository>();
            services.AddScoped<IShoppingListRepository, NullShoppingListRepository>();

            // Rating repository + household member directory (plantry-zlwp.3): back RateRecipe,
            // ClearRecipeRating, and GetRecipeRatingBreakdownQuery — all resolved by DetailsModel now —
            // with an in-memory fake seeded from Ratings/Members, no real RecipesDbContext/Identity
            // connection required.
            services.RemoveAll<IRecipeRatingRepository>();
            services.AddSingleton<IRecipeRatingRepository>(new FakeDetailRatingRepository(Ratings));
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeDetailHouseholdMemberReader(Members));
        });
    }
}

/// <summary>
/// Variant (plantry-zlwp.3): three-member household, I've rated 4 stars and Alex rated 5 (Sam hasn't) —
/// exercises the household summary line, the warm --in pill flavour (my rating is included), and the
/// popover's "not rated" row.
/// </summary>
public sealed class RecipeDetailRatedMultiMemberFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyList<RecipeRating> Ratings => RecipeDetailFixture.RatedByMeAndAlex(Recipe.Id);
    protected override IReadOnlyList<HouseholdMember> Members => RecipeDetailFixture.ThreeMemberHousehold();
}

/// <summary>
/// Variant (plantry-zlwp.3): three-member household, only Alex has rated (5 stars) — I haven't. Exercises
/// the grey-ghost --out pill flavour (my rating is NOT included in the average).
/// </summary>
public sealed class RecipeDetailRatedByOthersOnlyFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyList<RecipeRating> Ratings => RecipeDetailFixture.RatedByAlexOnly(Recipe.Id);
    protected override IReadOnlyList<HouseholdMember> Members => RecipeDetailFixture.ThreeMemberHousehold();
}

/// <summary>
/// Variant (plantry-zlwp.3): a single-member household (no directory entries) where that lone member has
/// rated the recipe — proves the household line stays suppressed even though RatedCount > 0 (epic:
/// "single-member household: no household line at all" — there's nothing to average).
/// </summary>
public sealed class RecipeDetailSingleMemberRatedFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyList<RecipeRating> Ratings =>
    [
        RecipeRating.Create(
            HouseholdId.From(RecipeDetailFixture.HouseholdAId),
            Recipe.Id,
            RecipeDetailFixture.CurrentUserId,
            5,
            Plantry.SharedKernel.Domain.SystemClock.Instance),
    ];
}

/// <summary>
/// Seeded, mutable in-memory <see cref="IRecipeRatingRepository"/> for the Detail L4 tests
/// (plantry-zlwp.3) — supports the full rate/clear round-trip a POST test drives (unlike
/// <c>FakeBrowseRecipeRatingRepository</c>, which is read-only since Browse never mutates ratings).
/// </summary>
internal sealed class FakeDetailRatingRepository(IEnumerable<RecipeRating> seed) : IRecipeRatingRepository
{
    private readonly List<RecipeRating> _items = seed.ToList();

    public Task AddAsync(RecipeRating rating, CancellationToken ct = default)
    {
        _items.Add(rating);
        return Task.CompletedTask;
    }

    public void Remove(RecipeRating rating) => _items.RemoveAll(r => r.Id == rating.Id);

    public Task<RecipeRating?> FindAsync(RecipeId recipeId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(r => r.RecipeId == recipeId && r.UserId == userId));

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeRating>>(_items.Where(r => r.RecipeId == recipeId).ToList());

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeIdsAsync(
        IReadOnlyList<RecipeId> recipeIds, CancellationToken ct = default)
    {
        var wanted = recipeIds.ToHashSet();
        return Task.FromResult<IReadOnlyList<RecipeRating>>(_items.Where(r => wanted.Contains(r.RecipeId)).ToList());
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fixed household member directory for the Detail L4 tests (plantry-zlwp.3).</summary>
internal sealed class FakeDetailHouseholdMemberReader(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HouseholdMember>>(members.OrderBy(m => m.DisplayName).ToList());
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
/// Variant: only Pasta priced — Tomatoes AND Garlic un-priced — still <c>CostCompleteness.Partial</c>
/// but with TWO distinct missing products (plantry-rpg8). Pins the plural branch of the Partial popover's
/// bolded count, complementing the base factory's single-missing-product (singular) fixture.
/// </summary>
public sealed class RecipeDetailTwoMissingPricesFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesPastaOnly();
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
/// Variant: a single untracked ingredient (Salt) with a REAL authored quantity (2 ea) — plantry-cbww.
/// Distinct from <see cref="RecipeDetailAllUntrackedFactory"/>, whose untracked lines are all null-qty
/// ("to taste"). Proves an untracked ingredient's authored amount still renders alongside its
/// "untracked" sub-label rather than being suppressed purely because <c>Product.TrackStock</c> is false.
/// </summary>
public sealed class RecipeDetailUntrackedQuantityFactory : RecipeDetailFragmentFactory
{
    protected override Recipe BuildRecipe() => RecipeDetailFixture.BuildWithUntrackedQuantity();
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesNone();
}

/// <summary>
/// Variant: Garlic on hand as a weight (grams) while the recipe line is a count ("ea") with no conversion
/// path — the unit-gap render path (plantry-z2sr). The Garlic row must read "Can't compare units" with the
/// info-tone status and the explanatory popover, not the flat danger "Not in your pantry".
/// </summary>
public sealed class RecipeDetailUnitGapFactory : RecipeDetailFragmentFactory
{
    protected override IReadOnlyDictionary<Guid, ProductStock> Stock => RecipeDetailFixture.StockWithUnitGap(Today);
}

/// <summary>
/// Variant: a single ¼-cup ingredient in a <c>DisplayStyle.Fraction</c>-styled unit (plantry-95w5's repro
/// recipe) — overrides the base factory's catalog reader registration with one that also resolves the
/// "cup" unit's fraction style, proving the flag reaches <c>_IngredientRow</c>'s rendered client-side
/// scaler call rather than only the base factory's decimal-only unit set.
/// </summary>
public sealed class RecipeDetailFractionStyleFactory : RecipeDetailFragmentFactory
{
    protected override Recipe BuildRecipe() => RecipeDetailFixture.BuildWithFractionStyledUnit();
    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesNone();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Re-register over the base factory's catalog reader (Decimal-only unit set) with one that also
        // knows "cup" is Fraction-styled — the fact plantry-95w5's fix threads onto the rendered row.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(new FakeCatalogProductReader(
                RecipeDetailFixture.Products(),
                RecipeDetailFixture.UnitCodesWithCup(),
                RecipeDetailFixture.UnitDisplayStyles()));
        });
    }
}
