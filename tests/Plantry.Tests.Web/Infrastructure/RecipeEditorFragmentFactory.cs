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
    public Recipe EmptyRecipe { get; } = RecipeEditorFixture.BuildEmpty();
    public Recipe RichRecipe  { get; } = RecipeEditorFixture.BuildRich();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Non-Development: skips startup migrations/seeding and the Dev-pages gate.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Auth: header-driven test scheme (same pattern as ReviewFragmentFactory).
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Recipe repository: knows both fixture recipes, scoped by owning household.
            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeEditorRecipeRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    EmptyRecipe,
                    RichRecipe));

            // Tag repository: resolves the fixture tag id → name mapping for the edit GET.
            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(
                new FakeTagRepository(RecipeEditorFixture.TagNames()));

            // Catalog product reader: returns the fixture product summaries + unit codes + unit options.
            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions()));

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

// ── Additional fakes needed to satisfy AuthorRecipe's dependency graph ────────────────────────────

/// <summary>No-op <see cref="ICatalogWriter"/> — the L4 tests never exercise the POST path.</summary>
internal sealed class FakeCatalogWriter : ICatalogWriter
{
    public Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>Always-succeeds <see cref="IUnitConverter"/> — the L4 tests never exercise the POST path.</summary>
internal sealed class FakeUnitConverter : IUnitConverter
{
    public Task<Plantry.SharedKernel.Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
        Task.FromResult(Plantry.SharedKernel.Result<decimal>.Success(amount));
}
