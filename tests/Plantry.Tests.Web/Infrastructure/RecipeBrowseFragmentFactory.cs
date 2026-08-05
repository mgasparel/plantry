using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the recipe Browse page (P2-2c). Boots the real
/// <c>Plantry.Web</c> pipeline (routing, authorization, Razor rendering) but replaces all
/// Postgres-backed and cross-context seams with in-memory fakes:
/// <list type="bullet">
///   <item><see cref="IRecipeRepository"/> — returns three fixture recipes.</item>
///   <item><see cref="ITagRepository"/> — returns fixture tags for filter chips + mini-pills.</item>
///   <item><see cref="ICatalogProductReader"/> — returns the fixture product set.</item>
///   <item><see cref="IInventoryStockReader"/> — returns fixture stock snapshots.</item>
///   <item><see cref="IPriceReader"/> — returns fixture price points (Milk has no price).</item>
///   <item><see cref="IUnitConverter"/> — identity converter (same-unit).</item>
/// </list>
/// No database is touched; rendered HTML is deterministic.
/// </summary>
public class RecipeBrowseFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The fixture recipes (Pancakes, Omelette, Milk Shake), built ONCE so every registration below — the
    /// recipe repository AND (plantry-zlwp.4) a derived factory's <see cref="Ratings"/> override — shares the
    /// same runtime <see cref="Recipe.Id"/> values. Building fresh per registration (the pre-zlwp.4 shape)
    /// would let a rating fixture target an id the recipe repository never actually served.
    /// </summary>
    private readonly IReadOnlyList<Recipe> _recipes = RecipeBrowseFixture.BuildRecipes();

    public Recipe Pancakes => _recipes[0];
    public Recipe Omelette => _recipes[1];
    public Recipe MilkShake => _recipes[2];

    /// <summary>
    /// Household display currency the per-recipe cost cells render with (plantry-2x6e.2). Default USD; a derived
    /// factory overrides it to exercise the non-USD symbol path.
    /// </summary>
    protected virtual string DisplayCurrency => "USD";

    /// <summary>
    /// Existing <see cref="RecipeRating"/> rows across the fixture recipes (plantry-zlwp.4), keyed against
    /// <see cref="Pancakes"/>/<see cref="Omelette"/>/<see cref="MilkShake"/>'s runtime ids. Default empty
    /// (see <see cref="FakeBrowseRecipeRatingRepository"/>'s doc). A derived factory seeds this to exercise the
    /// gallery/grid rating pill render paths.
    /// </summary>
    protected virtual IReadOnlyList<RecipeRating> Ratings => [];

    /// <summary>
    /// Household member directory (plantry-zlwp.4) backing <see cref="IHouseholdMemberReader"/> — display
    /// names/initials for the rating popover breakdown. Default empty, mirroring
    /// <c>RecipeDetailFragmentFactory.Members</c>'s convention. A derived factory populates this alongside
    /// <see cref="Ratings"/> to exercise the multi-member popover.
    /// </summary>
    protected virtual IReadOnlyList<HouseholdMember> Members => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Household display currency (plantry-2x6e.2): deterministic fake so the browse cost cells resolve
            // without a real Identity DB.
            services.AddFakeDisplayCurrency(DisplayCurrency);
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme, same pattern as other L4 factories.
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: three fixture recipes (the SAME built instances Ratings targets — see _recipes).
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeBrowseRecipeRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    _recipes));

            // Tag repository: fixture tags (Vegetarian, Spicy).
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeBrowseTagRepository(RecipeBrowseFixture.Tags()));

            // Catalog product reader: fixture products.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeBrowseCatalogProductReader(RecipeBrowseFixture.Products()));

            // Inventory stock reader: fixture stock snapshots.
            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeBrowseStockReader(RecipeBrowseFixture.Stock()));

            // Price reader: fixture price points (Milk has no price → NoCost recipe).
            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(
                new FakeBrowsePriceReader(RecipeBrowseFixture.Prices()));

            // Unit converter: identity (ingredient unit == product default unit in fixture).
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeBrowseUnitConverter());

            // Substitution reader (plantry-aqpa.2): empty — no fixture scenario exercises substitution
            // edges yet. Without this override FulfillmentService resolves the real Postgres-backed
            // SubstitutionReader, which this factory's no-database setup cannot satisfy.
            services.RemoveAll<ISubstitutionReader>();
            services.AddSingleton<ISubstitutionReader>(new FakeBrowseSubstitutionReader());

            // Recipe rating repository (plantry-zlwp.1): BrowseRecipesQuery now batch-loads ratings per
            // row; the real RecipeRatingRepository needs a live RecipesDbContext/Postgres connection,
            // so it is replaced here like every other Postgres-backed seam above. Empty by default (see
            // Ratings) — no fixture recipe has been rated, so MyStars/HouseholdAvg/RatedCount are null/0
            // on every row. A derived factory seeds Ratings to exercise the rating pill render paths.
            services.RemoveAll<IRecipeRatingRepository>();
            services.AddSingleton<IRecipeRatingRepository>(new FakeBrowseRecipeRatingRepository(Ratings));

            // Household member directory (plantry-zlwp.4): backs BrowseRecipesQuery's per-row popover
            // breakdown. The real adapter is Postgres-backed (IHouseholdDirectory), so it's replaced here
            // like every other cross-context seam above. Default empty; a derived factory seeds Members.
            services.RemoveAll<IHouseholdMemberReader>();
            services.AddSingleton<IHouseholdMemberReader>(new FakeBrowseHouseholdMemberReader(Members));

            // AuthorRecipe is registered in Program.cs and requires ICatalogWriter — replaced below.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());
        });
    }
}

/// <summary>
/// Variant: the browse cost cells rendered for a EUR household (plantry-2x6e.2) — proves the per-recipe cost
/// renders MoneyDisplay's '€' symbol.
/// </summary>
public sealed class RecipeBrowseEurFactory : RecipeBrowseFragmentFactory
{
    protected override string DisplayCurrency => "EUR";
}

/// <summary>
/// Variant (plantry-zlwp.4): exercises all three gallery/grid rating render paths across the fixture's
/// three recipes in one factory — Pancakes (I've rated: filled "mine" pill), Omelette (only Alex has
/// rated: grey ghost pill), Milk Shake (nobody has rated: no pill / dash). See
/// <see cref="RecipeBrowseFixture.RatedPancakesAndOmelette"/>.
/// </summary>
public sealed class RecipeBrowseRatedFactory : RecipeBrowseFragmentFactory
{
    protected override IReadOnlyList<RecipeRating> Ratings =>
        RecipeBrowseFixture.RatedPancakesAndOmelette(Pancakes.Id, Omelette.Id);

    protected override IReadOnlyList<HouseholdMember> Members => RecipeBrowseFixture.TwoMemberHousehold();
}

/// <summary>
/// In-memory <see cref="ICatalogProductReader"/> for Browse tests — used by
/// <see cref="FulfillmentService"/> to resolve TrackStock flag per product.
/// </summary>
internal sealed class FakeBrowseCatalogProductReader(IReadOnlyDictionary<Guid, CatalogProduct> products)
    : ICatalogProductReader
{
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(products.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(id, products[id].Name, products[id].TrackStock));
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);

    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
}

/// <summary>
/// Read-only in-memory <see cref="IRecipeRatingRepository"/> for Browse tests — seeded from a fixed set
/// (plantry-zlwp.4). Browse never mutates ratings, so unlike <c>FakeDetailRatingRepository</c> this stays
/// read-only over the seed rather than a mutable list. Default seed is empty (no fixture recipe rated).
/// </summary>
internal sealed class FakeBrowseRecipeRatingRepository(IReadOnlyList<RecipeRating> seed) : IRecipeRatingRepository
{
    public FakeBrowseRecipeRatingRepository() : this([]) { }

    public Task AddAsync(RecipeRating rating, CancellationToken ct = default) => Task.CompletedTask;

    public void Remove(RecipeRating rating) { }

    public Task<RecipeRating?> FindAsync(RecipeId recipeId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(seed.SingleOrDefault(r => r.RecipeId == recipeId && r.UserId == userId));

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeRating>>(seed.Where(r => r.RecipeId == recipeId).ToList());

    public Task<IReadOnlyList<RecipeRating>> ListByRecipeIdsAsync(
        IReadOnlyList<RecipeId> recipeIds, CancellationToken ct = default)
    {
        var wanted = recipeIds.ToHashSet();
        return Task.FromResult<IReadOnlyList<RecipeRating>>(seed.Where(r => wanted.Contains(r.RecipeId)).ToList());
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fixed household member directory for Browse L4 tests (plantry-zlwp.4), mirroring
/// <c>FakeDetailHouseholdMemberReader</c>.</summary>
internal sealed class FakeBrowseHouseholdMemberReader(IReadOnlyList<HouseholdMember> members) : IHouseholdMemberReader
{
    public Task<IReadOnlyList<HouseholdMember>> ListMembersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HouseholdMember>>(members.OrderBy(m => m.DisplayName).ToList());
}
