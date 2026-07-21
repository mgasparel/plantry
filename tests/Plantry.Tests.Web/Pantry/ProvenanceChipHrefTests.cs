using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Catalog;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for plantry-72c6: the pantry Product Detail History grid's provenance
/// chip href, now built Web-side via <c>Url.Page</c> off the structured <see cref="ProvenanceChip"/>
/// (<see cref="StockProvenanceReaderAdapter"/> in Plantry.Composition carries only raw target ids —
/// no URL knowledge). <see cref="FakeProvenanceReader"/> stands in for the real composition-root
/// adapter (covered separately by <c>StockProvenanceReaderAdapterTests</c>) so this test isolates the
/// Web-side href-building concern: at root hosting, the rendered anchor must be byte-identical to the
/// pre-plantry-72c6 hardcoded string for both chip kinds, including the Intake chip's
/// <c>#line-{id}</c> fragment.
/// </summary>
public sealed class ProvenanceChipHrefTests : IDisposable
{
    private readonly ProvenanceChipHrefFactory _factory = new();
    private static readonly HtmlParser Parser = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ProvenanceChipHrefFixture.HouseholdId.ToString());
        return client;
    }

    [Fact(DisplayName = "History grid — Intake chip href is Url.Page-built, byte-identical to the old hardcoded /Intake/Session/{id}#line-{id}")]
    public async Task IntakeChip_RendersByteIdenticalHref()
    {
        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProvenanceChipHrefFixture.ProductId}"))
            .Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var chip = doc.QuerySelectorAll("a.src-chip")
            .FirstOrDefault(a => a.TextContent.Contains("Costco"));
        Assert.NotNull(chip);
        Assert.Equal(
            $"/Intake/Session/{ProvenanceChipHrefFixture.IntakeSessionId}#line-{ProvenanceChipHrefFixture.IntakeLineId}",
            chip!.GetAttribute("href"));
    }

    [Fact(DisplayName = "History grid — Cook chip href is Url.Page-built, byte-identical to the old hardcoded /Recipes/{id}")]
    public async Task CookChip_RendersByteIdenticalHref()
    {
        var client = AuthClient();
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{ProvenanceChipHrefFixture.ProductId}"))
            .Content.ReadAsStringAsync();
        var doc = Parser.ParseDocument(html);

        var chip = doc.QuerySelectorAll("a.src-chip")
            .FirstOrDefault(a => a.TextContent.Contains("Shakshuka"));
        Assert.NotNull(chip);
        Assert.Equal($"/Recipes/{ProvenanceChipHrefFixture.RecipeId}", chip!.GetAttribute("href"));
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProvenanceChipHrefFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

    internal static readonly Guid ProductId = Guid.Parse("aaaaaaaa-0000-0000-0000-aaa000000099");
    internal static readonly Guid UnitId = Guid.Parse("bbbbbbbb-0000-0000-0000-bbb000000099");
    internal static readonly Guid LocationId = Guid.NewGuid();
    internal static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    // The fake reader's canned chip targets — what the Web-side HrefFor should resolve into hrefs.
    internal static readonly Guid IntakeSessionId = Guid.NewGuid();
    internal static readonly Guid IntakeLineId = Guid.NewGuid();
    internal static readonly Guid RecipeId = Guid.NewGuid();

    internal static ProductStock BuildStockWithHistory()
    {
        var stock = ProductStock.Start(Household, ProductId, Clock);
        // One Intake-sourced journal row and one Cook-sourced journal row — exactly the two chip
        // kinds the Web side must resolve to a link.
        stock.AddStock(2m, UnitId, LocationId, UserId, Clock, sourceType: StockSourceType.Intake, sourceRef: Guid.NewGuid());
        stock.AddStock(1m, UnitId, LocationId, UserId, Clock, sourceType: StockSourceType.Cook, sourceRef: Guid.NewGuid());
        return stock;
    }
}

/// <summary>
/// A canned <see cref="IStockProvenanceReader"/> standing in for the real composition-root adapter
/// (<c>StockProvenanceReaderAdapter</c>, tested separately) — this test isolates the Web-side
/// href-building concern, so any Intake row resolves to a fixed session/line chip and any Cook row
/// to a fixed recipe chip.
/// </summary>
internal sealed class FakeProvenanceReader : IStockProvenanceReader
{
    public Task<IReadOnlyDictionary<Guid, ProvenanceChip>> ResolveAsync(
        IReadOnlyList<(Guid JournalId, StockSourceType SourceType, Guid? SourceRef)> rows,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ProvenanceChip> result = rows.ToDictionary(
            r => r.JournalId,
            r => r.SourceType switch
            {
                StockSourceType.Intake => new ProvenanceChip(
                    ProvenanceChipKind.Intake, "Costco · 18 Jul",
                    ProvenanceChipHrefFixture.IntakeSessionId, ProvenanceChipHrefFixture.IntakeLineId),
                StockSourceType.Cook => new ProvenanceChip(
                    ProvenanceChipKind.Cook, "Shakshuka", ProvenanceChipHrefFixture.RecipeId),
                _ => throw new InvalidOperationException($"Unexpected source type {r.SourceType} in test fixture."),
            });
        return Task.FromResult(result);
    }
}

/// <summary>In-memory <see cref="IUnitRepository"/> that always reports no units — the Detail GET path
/// never calls it, but the page constructor still requires one registered.</summary>
internal sealed class FakeEmptyUnitRepository : IUnitRepository
{
    public Task<Unit?> FindAsync(UnitId id, CancellationToken ct = default) => Task.FromResult<Unit?>(null);
    public Task<Unit?> FindByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<Unit?>(null);
    public Task<List<Unit>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Unit>());
    public Task AddAsync(Unit unit, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// L4 <see cref="WebApplicationFactory{TEntryPoint}"/> for plantry-72c6's provenance-chip href tests.
/// Replaces <see cref="IProductStockRepository"/> (reusing <c>FakeDetailStockRepository</c> from the
/// Catalog Detail tests), <see cref="ICatalogReadFacade"/>/<see cref="IProductConversionProvider"/>
/// (reusing the Today-page fakes — this page's GET path only needs their default empty/identity
/// behaviour), and <see cref="IStockProvenanceReader"/> with the canned <see cref="FakeProvenanceReader"/>.
/// No EF / Postgres / Intake / Recipes wiring touched.
/// </summary>
internal sealed class ProvenanceChipHrefFactory : WebApplicationFactory<Program>
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

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(ProvenanceChipHrefFixture.BuildStockWithHistory());
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeTodayCatalogReadFacade());

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new FakeTodayConversionProvider());

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeEmptyUnitRepository());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeProvenanceReader());
        });
    }
}
