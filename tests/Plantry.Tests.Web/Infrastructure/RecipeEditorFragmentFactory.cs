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
/// L4 WebApplicationFactory for the recipe editor (create + edit) page. Boots the real
/// <c>Plantry.Web</c> pipeline (routing, authorization, Razor rendering) but replaces the
/// recipe-, tag-, catalog-, and application-service seams the editor GET depends on with
/// in-memory fakes, and swaps cookie auth for the header-driven test scheme.
///
/// <para>Two fixture recipes are registered: an empty-ingredient one (for the create/empty-edit
/// scenario) and a rich one (multiple groups, tags, untracked staple, directions). Tests choose
/// which id to request.</para>
///
/// <para><see cref="AuthorRecipe"/> is built via DI over the same fakes — it is only exercised
/// on the GET path (where it is not called), so the fakes for <see cref="ICatalogWriter"/> and
/// <see cref="IUnitConverter"/> are no-ops. If a POST snapshot test is added in future, these
/// fakes already satisfy the full dependency graph.</para>
/// </summary>
public sealed class RecipeEditorFragmentFactory : WebApplicationFactory<Program>
{
    public Recipe EmptyRecipe           { get; } = RecipeEditorFixture.BuildEmpty();
    public Recipe RichRecipe            { get; } = RecipeEditorFixture.BuildRich();
    public Recipe RichArchivedTagRecipe { get; } = RecipeEditorFixture.BuildRichWithArchivedTag();
    public Recipe NonCanonicalRecipe    { get; } = RecipeEditorFixture.BuildNonCanonical();
    public Recipe FlipToTrackedRecipe   { get; } = RecipeEditorFixture.BuildFlipToTracked();
    public Recipe PhotoRecipe           { get; } = RecipeEditorFixture.BuildWithPhoto();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Non-Development: skips startup migrations/seeding and the Dev-pages gate.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            // Auth: header-driven test scheme (same pattern as ReviewFragmentFactory).
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: knows all fixture recipes, scoped by owning household.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeEditorRecipeRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    EmptyRecipe,
                    RichRecipe,
                    RichArchivedTagRecipe,
                    NonCanonicalRecipe,
                    FlipToTrackedRecipe,
                    PhotoRecipe));

            // Tag repository: resolves the fixture tag id → name mapping for the edit GET, and
            // serves the full tag list (active + archived) for FakeTagRepository.
            // ListAllAsync(activeOnly:true) filters out archived tags in-memory, so the picker
            // dropdown only surfaces active tags even with AllTagsIncludingArchived() seeded.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeEditorFixture.TagNames(), RecipeEditorFixture.AllTagsIncludingArchived()));

            // Catalog product reader: returns the fixture product summaries + unit codes + unit options.
            // ProductDefaultUnits() is supplied (plantry-obg3) so the edit GET seeds each landed row with
            // its product's REAL default unit (g for Rigatoni/Tomatoes, ea for Garlic/Chili) rather than
            // the FirstOrDefault fallback that made every row "g".
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

            // Catalog writer + unit converter: replaced with no-op fakes so AuthorRecipe (which the
            // editor page model injects) resolves. The GET path never calls these — they are present
            // only to satisfy the dependency graph.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());
        });
    }
}

/// <summary>
/// WAF variant that serves NO active tags (empty household tag list) so FIX 2 tests can assert
/// that the recipe editor renders the guidance message + <c>/Settings/Tags</c> link.
/// </summary>
public sealed class RecipeEditorEmptyTagsFactory : WebApplicationFactory<Program>
{
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
                new FakeEditorRecipeRepository(sp.GetRequiredService<ITenantContext>()));

            // No active tags — empty list causes LoadReferenceDataAsync to produce TagOptions:[]
            // which triggers the server-side guidance block in Edit.cshtml.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(new Dictionary<TagId, string>(), []));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions()));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());
        });
    }
}

/// <summary>
/// WAF variant used exclusively for the OnPost tag-picker round-trip test (plantry-uod FIX 1).
/// Registers the recipe repository as a singleton so the test can inspect what was persisted via
/// <see cref="RecipeRepo"/> after the POST completes.
/// </summary>
public sealed class RecipeEditorPostFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Singleton repository exposed to the test so it can assert what <see cref="AuthorRecipe"/> persisted.
    /// </summary>
    public FakeEditorRecipeRepository RecipeRepo { get; } =
        new(new ConstantTenantContext(RecipeEditorFixture.HouseholdAId));

    /// <summary>
    /// Singleton catalog writer exposed so the four-field conversion POST test (plantry-qno9) can assert
    /// the server-computed (productId, from = left unit, to = right unit, factor = right/left) triple.
    /// </summary>
    internal FakeCatalogWriter CatalogWriter { get; } = new();

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

            // Singleton so the test can inspect LastAdded after the POST.
            services.RemoveAll<IRecipeRepository>();
            services.AddSingleton<IRecipeRepository>(RecipeRepo);

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeEditorFixture.TagNames(), RecipeEditorFixture.AllTagsIncludingArchived()));

            // Pass productDefaultUnits so FindAsync returns a product with a matching DefaultUnitId,
            // preventing spurious NeedsConversion results when UnitId == DefaultUnitId.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(CatalogWriter);

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeUnitConverter());
        });
    }
}

// ── Additional fakes needed to satisfy AuthorRecipe's dependency graph ────────────────────────────

/// <summary>
/// <see cref="ICatalogWriter"/> test double for the editor L4 tests. Creates are no-ops returning a
/// fresh id; <see cref="AddConversionAsync"/> records its arguments in <see cref="ConversionsAdded"/>
/// so the plantry-qno9 four-field POST test can assert the server computed factor = right/left and
/// wrote (from = left unit, to = right unit).
/// </summary>
internal sealed class FakeCatalogWriter : ICatalogWriter
{
    public List<(Guid ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor)> ConversionsAdded { get; } = [];

    public Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task<Guid> CreateTrackedProductAsync(string name, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task<Guid> CreateTrackedVariantAsync(Guid parentGroupId, string variantName, Guid? unitOverride, Guid? categoryOverride, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task<Guid> CreateTrackedGroupedProductAsync(string groupName, string variantName, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        ConversionsAdded.Add((productId, fromUnitId, toUnitId, factor));
        return Task.CompletedTask;
    }
}

/// <summary>Always-succeeds <see cref="IUnitConverter"/> — the L4 tests never exercise the POST path.</summary>
internal sealed class FakeUnitConverter : IUnitConverter
{
    public Task<Plantry.SharedKernel.Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(Plantry.SharedKernel.Result<decimal>.Success(amount));
}

/// <summary>
/// Constant <see cref="ITenantContext"/> that always returns the given household id.
/// Used for the singleton <see cref="FakeEditorRecipeRepository"/> in the POST test factory
/// where the real per-request tenant is not available at singleton construction time.
/// </summary>
internal sealed class ConstantTenantContext(Guid householdId) : Plantry.SharedKernel.Tenancy.ITenantContext
{
    public Guid? HouseholdId => householdId;
}
