using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Background;

namespace Plantry.Tests.Web;

/// <summary>
/// Editor-level tests for the edit-moment AI conversion deferral (plantry-qll2.4). With a converter that
/// cannot bridge a cross-dimension line (ea against a gram-stocked product) and no factor supplied:
/// <list type="bullet">
///   <item>AI toggle ON + a real inferrer available → the recipe SAVES (redirect, no NeedsConversion
///   prompt) and a background conversion seed is enqueued (criterion 1);</item>
///   <item>AI toggle OFF → today's behaviour: the save is blocked with the inline conversion prompt and
///   nothing is enqueued (criterion 4).</item>
/// </list>
/// The capturing queue records the enqueued work item without running it, so the test needs no database —
/// it proves the editor→trigger wiring, while <c>RecipeConversionSeederTests</c> covers what the item does.
/// </summary>
public sealed class RecipeEditorConversionDeferTests
{
    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeEditorFixture.HouseholdAId.ToString());
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Recipes/New")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the create page.");
        return match.Groups[1].Value;
    }

    private static List<KeyValuePair<string, string>> CrossDimensionPost(string token) =>
    [
        new("__RequestVerificationToken", token),
        new("Input.Name",            "Cashew Bowl"),
        new("Input.DefaultServings", "2"),
        new("Input.Lines[0].Ordinal",   "0"),
        new("Input.Lines[0].ProductId", RecipeEditorFixture.PastaId.ToString()),  // stocked in g
        new("Input.Lines[0].Quantity",  "2"),
        new("Input.Lines[0].UnitId",    RecipeEditorFixture.EachUnitId.ToString()), // ea → cross-dimension gap
        // No conversion factor / equation supplied — the converter cannot bridge it.
    ];

    [Fact]
    public async Task Toggle_On_With_Inferrer_Saves_With_Gap_And_Enqueues_A_Seed()
    {
        using var factory = new DeferFactory(aiEnabled: true, inferrerAvailable: true);
        var client = AuthenticatedClient(factory);
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync(
            "/Recipes/New", new FormUrlEncodedContent(CrossDimensionPost(token)));

        // Saved → redirect to Details, NOT a re-render with the inline conversion prompt.
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after deferred save, got {(int)response.StatusCode}.");
        Assert.Contains("/Recipes/", response.Headers.Location!.ToString());

        // A background conversion seed was enqueued for the gap.
        Assert.Equal(1, factory.Queue.EnqueuedCount);
    }

    [Fact]
    public async Task Toggle_Off_Keeps_Todays_Conversion_Prompt_And_Enqueues_Nothing()
    {
        using var factory = new DeferFactory(aiEnabled: false, inferrerAvailable: true);
        var client = AuthenticatedClient(factory);
        var token = await GetAntiforgeryTokenAsync(client);

        var response = await client.PostAsync(
            "/Recipes/New", new FormUrlEncodedContent(CrossDimensionPost(token)));

        // Blocked: re-rendered form (200) with the inline conversion prompt — today's behaviour.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("need a conversion factor", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.Queue.EnqueuedCount);
    }

    // ── Factory + fakes ────────────────────────────────────────────────────────────

    private sealed class DeferFactory(bool aiEnabled, bool inferrerAvailable) : WebApplicationFactory<Program>
    {
        public CapturingBackgroundTaskQueue Queue { get; } = new();

        private FakeEditorRecipeRepository RecipeRepo { get; } =
            new(new ConstantTenantContext(RecipeEditorFixture.HouseholdAId));

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
                services.AddSingleton<IRecipeRepository>(RecipeRepo);

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(
                    RecipeEditorFixture.TagNames(), RecipeEditorFixture.AllTagsIncludingArchived()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeEditorProductReader(
                    RecipeEditorFixture.ProductSummaries(),
                    RecipeEditorFixture.UnitCodes(),
                    RecipeEditorFixture.UnitOptions(),
                    RecipeEditorFixture.ProductDefaultUnits()));

                services.RemoveAll<ICatalogWriter>();
                services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

                // Converter that never finds a path → every cross-unit line is a gap.
                services.RemoveAll<IUnitConverter>();
                services.AddSingleton<IUnitConverter>(new NoPathUnitConverter());

                // AI seams under test.
                services.RemoveAll<IIngredientConversionInferrer>();
                services.AddSingleton<IIngredientConversionInferrer>(
                    new FakeInferrer(inferrerAvailable));

                services.RemoveAll<IAiAssistanceGateReader>();
                services.AddSingleton<IAiAssistanceGateReader>(new FakeGateReader(aiEnabled));

                services.RemoveAll<IBackgroundTaskQueue>();
                services.AddSingleton<IBackgroundTaskQueue>(Queue);
            });
        }
    }

    private sealed class NoPathUnitConverter : IUnitConverter
    {
        public Task<Result<decimal>> ConvertAsync(
            Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default) =>
            fromUnitId == toUnitId
                ? Task.FromResult(Result<decimal>.Success(amount))
                : Task.FromResult(Result<decimal>.Failure(
                    Error.Custom("Catalog.UnresolvableConversion", "No conversion path.")));
    }

    private sealed class FakeInferrer(bool available) : IIngredientConversionInferrer
    {
        public bool IsAvailable => available;
        public Task<decimal?> InferFactorAsync(
            string productName, string fromUnitCode, string toUnitCode, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(120m);
    }

    private sealed class FakeGateReader(bool enabled) : IAiAssistanceGateReader
    {
        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(enabled);
    }

    /// <summary>Records enqueued work items without running them; Dequeue blocks so the hosted drainer stays idle.</summary>
    private sealed class CapturingBackgroundTaskQueue : IBackgroundTaskQueue
    {
        private int _count;
        public int EnqueuedCount => Volatile.Read(ref _count);

        public ValueTask EnqueueAsync(
            Func<IServiceProvider, CancellationToken, Task> workItem, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }
}
