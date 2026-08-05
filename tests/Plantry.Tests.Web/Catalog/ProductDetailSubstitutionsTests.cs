using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Pantry.Domain.Unit;
using SystemClock = Plantry.SharedKernel.Domain.SystemClock;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for the "Substitutions" section on the Catalog Product Detail page
/// (plantry-aqpa.5) — the household-authored, directed, unit-bearing edges Recipes owns
/// (<see cref="Substitution"/>), authored here via composition (the page model calls Recipes'
/// application seams directly, mirroring Cook.cshtml.cs's existing pattern).
///
/// The catalog/inventory seams and the Substitution reader/repository are in-memory fakes; no DB.
/// </summary>
public sealed class ProductDetailSubstitutionsTests : IDisposable
{
    private readonly SubstitutionsFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, SubstitutionsFactory.HouseholdId.ToString());
        return client;
    }

    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Catalog/Products/{productId}"))
            .Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    // AC: the list shows every edge touching this product, both directions, phrased from this
    //     product's point of view.
    [Fact(DisplayName = "Substitutions list shows both incoming and outgoing edges, phrased from this product's view")]
    public async Task List_Shows_Both_Directions()
    {
        var householdId = HouseholdId.From(SubstitutionsFactory.HouseholdId);
        var clock = SystemClock.Instance;

        // Force the WAF host to build (and _factory.UnitId to be assigned) before seeding edges that
        // reference it — CreateClient() triggers ConfigureWebHost lazily.
        var client = AuthClient();

        // Incoming: Rice Flour satisfies Flour (this product).
        _factory.Substitutions.Items.Add(Substitution.Create(
            householdId,
            targetProductId: _factory.FlourId, targetQuantity: 1m, targetUnitId: _factory.UnitId,
            substituteProductId: _factory.RiceFlourId, substituteQuantity: 1m, substituteUnitId: _factory.UnitId,
            clock));
        // Outgoing: Flour (this product) stands in for Almond Flour.
        _factory.Substitutions.Items.Add(Substitution.Create(
            householdId,
            targetProductId: _factory.AlmondFlourId, targetQuantity: 1m, targetUnitId: _factory.UnitId,
            substituteProductId: _factory.FlourId, substituteQuantity: 1m, substituteUnitId: _factory.UnitId,
            clock));

        var html = await (await client.GetAsync($"/Catalog/Products/{_factory.FlourId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Satisfied by Rice Flour", html);
        Assert.Contains("Stands in for Almond Flour", html);
    }

    // Guard: a Direction value that is neither "in" nor "out" (a tampered/garbled POST body) must
    // round-trip as a field error rather than being silently treated as "out" by the isIncoming check.
    [Fact(DisplayName = "AddSubstitution rejects an unrecognised Direction value instead of writing a reversed edge")]
    public async Task AddSubstitution_UnrecognisedDirection_RejectsWithoutWriting()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client, _factory.FlourId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{_factory.FlourId}?handler=AddSubstitution",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("SubstitutionInput.Direction", "banana"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherProductId", _factory.RiceFlourId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.ThisQuantity", "100"),
                new KeyValuePair<string, string>("SubstitutionInput.ThisUnitId", _factory.UnitId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.OtherQuantity", "260"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherUnitId", _factory.UnitId.ToString()),
            }));

        // Re-rendered page with the field error, not a redirect — no edge is authored either way.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.Substitutions.Items);
    }

    // AC: authoring an incoming edge ("another product satisfies this one") stores it with THIS
    //     product as the edge's target.
    [Fact(DisplayName = "AddSubstitution (incoming) creates an edge targeting this product")]
    public async Task AddSubstitution_Incoming_TargetsThisProduct()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client, _factory.FlourId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{_factory.FlourId}?handler=AddSubstitution",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("SubstitutionInput.Direction", "in"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherProductId", _factory.RiceFlourId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.ThisQuantity", "100"),
                new KeyValuePair<string, string>("SubstitutionInput.ThisUnitId", _factory.UnitId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.OtherQuantity", "260"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherUnitId", _factory.UnitId.ToString()),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var edge = Assert.Single(_factory.Substitutions.Items);
        Assert.Equal(_factory.FlourId, edge.TargetProductId);
        Assert.Equal(100m, edge.TargetQuantity);
        Assert.Equal(_factory.RiceFlourId, edge.SubstituteProductId);
        Assert.Equal(260m, edge.SubstituteQuantity);
    }

    // AC: authoring an outgoing edge ("this product satisfies another") stores it with THIS product
    //     as the edge's substitute.
    [Fact(DisplayName = "AddSubstitution (outgoing) creates an edge with this product as the substitute")]
    public async Task AddSubstitution_Outgoing_MakesThisProductTheSubstitute()
    {
        var client = AuthClient();
        var token = await GetAntiforgeryTokenAsync(client, _factory.FlourId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{_factory.FlourId}?handler=AddSubstitution",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("SubstitutionInput.Direction", "out"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherProductId", _factory.AlmondFlourId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.ThisQuantity", "1"),
                new KeyValuePair<string, string>("SubstitutionInput.ThisUnitId", _factory.UnitId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.OtherQuantity", "1"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherUnitId", _factory.UnitId.ToString()),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var edge = Assert.Single(_factory.Substitutions.Items);
        Assert.Equal(_factory.AlmondFlourId, edge.TargetProductId);
        Assert.Equal(_factory.FlourId, edge.SubstituteProductId);
    }

    // AC: "Duplicate directed pair: surface the domain rule (replace or reject) as a clear inline
    //     message" — CreateSubstitution replaces on duplicate; the toast must say so distinctly.
    [Fact(DisplayName = "AddSubstitution over an existing directed pair replaces the ratio and says so")]
    public async Task AddSubstitution_DuplicatePair_ReplacesAndSaysSo()
    {
        var householdId = HouseholdId.From(SubstitutionsFactory.HouseholdId);
        var client = AuthClient(); // builds the host so _factory.UnitId is assigned before seeding below
        _factory.Substitutions.Items.Add(Substitution.Create(
            householdId,
            targetProductId: _factory.FlourId, targetQuantity: 100m, targetUnitId: _factory.UnitId,
            substituteProductId: _factory.RiceFlourId, substituteQuantity: 200m, substituteUnitId: _factory.UnitId,
            SystemClock.Instance));

        var token = await GetAntiforgeryTokenAsync(client, _factory.FlourId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{_factory.FlourId}?handler=AddSubstitution",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("SubstitutionInput.Direction", "in"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherProductId", _factory.RiceFlourId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.ThisQuantity", "100"),
                new KeyValuePair<string, string>("SubstitutionInput.ThisUnitId", _factory.UnitId.ToString()),
                new KeyValuePair<string, string>("SubstitutionInput.OtherQuantity", "260"),
                new KeyValuePair<string, string>("SubstitutionInput.OtherUnitId", _factory.UnitId.ToString()),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // Still one edge (upsert, not a second row) with the NEW ratio.
        var edge = Assert.Single(_factory.Substitutions.Items);
        Assert.Equal(260m, edge.SubstituteQuantity);

        // Follow the redirect with the same client (its cookie container carries the TempData cookie
        // the POST response set, mirroring ProductDetailToastTests' PostAndFollowAsync pattern) and
        // check the toast wording distinguishes "replaced" from "added".
        var html = await (await client.GetAsync(response.Headers.Location)).Content.ReadAsStringAsync();
        Assert.Contains("replaced the existing ratio", html);
    }

    // AC: "Delete with confirm" — the Remove form posts to RemoveSubstitution and deletes the edge.
    [Fact(DisplayName = "RemoveSubstitution deletes the edge and redirects")]
    public async Task RemoveSubstitution_DeletesTheEdge()
    {
        var householdId = HouseholdId.From(SubstitutionsFactory.HouseholdId);
        var client = AuthClient(); // builds the host so _factory.UnitId is assigned before seeding below
        var edge = Substitution.Create(
            householdId,
            targetProductId: _factory.FlourId, targetQuantity: 100m, targetUnitId: _factory.UnitId,
            substituteProductId: _factory.RiceFlourId, substituteQuantity: 260m, substituteUnitId: _factory.UnitId,
            SystemClock.Instance);
        _factory.Substitutions.Items.Add(edge);

        var token = await GetAntiforgeryTokenAsync(client, _factory.FlourId);

        var response = await client.PostAsync(
            $"/Catalog/Products/{_factory.FlourId}?handler=RemoveSubstitution&substitutionId={edge.Id.Value}",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(_factory.Substitutions.Items);
    }

    // Confirm requirement — the Remove control must carry a JS confirm() guard (house convention,
    // matches Archive on the same page), so a stray click can't silently delete an edge.
    [Fact(DisplayName = "Remove control carries a confirm() guard")]
    public async Task RemoveControl_HasConfirmGuard()
    {
        var householdId = HouseholdId.From(SubstitutionsFactory.HouseholdId);
        var client = AuthClient(); // builds the host so _factory.UnitId is assigned before seeding below
        _factory.Substitutions.Items.Add(Substitution.Create(
            householdId,
            targetProductId: _factory.FlourId, targetQuantity: 100m, targetUnitId: _factory.UnitId,
            substituteProductId: _factory.RiceFlourId, substituteQuantity: 260m, substituteUnitId: _factory.UnitId,
            SystemClock.Instance));

        var html = await (await client.GetAsync($"/Catalog/Products/{_factory.FlourId}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("handler=RemoveSubstitution", html);
        Assert.Contains("confirm(", html);
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class SubstitutionsFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid HouseholdId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Plantry.SharedKernel.HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    private static readonly IClock Clock = SystemClock.Instance;

    internal FakeProductRepo ProductRepo { get; } = new();
    internal FakeSubstitutionRepository Substitutions { get; } = new();

    /// <summary>
    /// The seeded unit's and products' ids — all generated by <c>Unit.Create</c>/<c>Product.Create</c>,
    /// so tests read them here rather than assuming fixed constants. Unlike
    /// <see cref="ProductDetailAddVariantFixture"/>'s pattern (where <see cref="FakeProductRepo.AddWithId"/>
    /// remaps a fixed fixture id onto <c>FindAsync</c> lookups only), this feature also reads products
    /// through <c>ListActiveAsync</c>/<c>ListByIdsAsync</c> (the substitution product picker + the
    /// "other product" name resolution) — those return each product keyed by its OWN generated Id, so a
    /// remapped fixture id would silently disagree with the route/FindAsync id. Seeding AddWithId with
    /// each product's own real id keeps every lookup path consistent.
    /// </summary>
    internal Guid UnitId { get; private set; }
    internal Guid FlourId { get; private set; }
    internal Guid RiceFlourId { get; private set; }
    internal Guid AlmondFlourId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddFakeHouseholdExpiryDefaults();
            services.AddFakeSubstitutions(Substitutions);
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var each = CatalogUnit.Create(Household, "g", "Grams", Dimension.Mass, 1m, isBase: true);
            UnitId = each.Id.Value;

            var flour = Product.Create(Household, "Flour", each.Id, Clock);
            var riceFlour = Product.Create(Household, "Rice Flour", each.Id, Clock);
            var almondFlour = Product.Create(Household, "Almond Flour", each.Id, Clock);
            FlourId = flour.Id.Value;
            RiceFlourId = riceFlour.Id.Value;
            AlmondFlourId = almondFlour.Id.Value;
            ProductRepo.AddWithId(flour, FlourId);
            ProductRepo.AddWithId(riceFlour, RiceFlourId);
            ProductRepo.AddWithId(almondFlour, AlmondFlourId);

            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => ProductRepo);

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeUnitListRepository(each));

            services.RemoveAll<ICategoryRepository>();
            services.AddSingleton<ICategoryRepository>(new FakeEmptyCategoryRepository());

            services.RemoveAll<ILocationRepository>();
            services.AddSingleton<ILocationRepository>(new FakeEmptyLocationRepository());

            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(new FakeDetailStockRepository());

            services.RemoveAll<ProductQueryService>();
            services.AddScoped<ProductQueryService>();
        });
    }
}
