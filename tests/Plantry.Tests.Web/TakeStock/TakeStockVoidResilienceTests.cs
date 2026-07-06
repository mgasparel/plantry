using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.TakeStock;

/// <summary>
/// Regression test for plantry-d5cp: a per-product deferred-unit-gap void failure inside
/// <c>WalkModel.OnPostSaveAsync</c> must NOT abandon the rest of the batch.
///
/// <para>The void loop supersedes any outstanding <see cref="CookConsumeLineStatus.DeferredUnitGap"/>
/// lines for each successfully-counted product (plantry-qll2.6). Voids are the load-bearing safety
/// rule of deferred consume and — unlike applies — have no cook-entry self-heal net, so a dropped
/// void silently double-counts when a conversion later lands. The try/catch must therefore live
/// INSIDE the foreach (matching NoLocation.cshtml.cs), so one product's <c>VoidDeferredUnitGapLines</c>
/// failure is logged and skipped while every sibling's void still runs.</para>
///
/// This test drives a two-product Save where the FIRST product's void throws and asserts the SECOND
/// product's void is still attempted (proving the loop did not abort on the first failure).
/// </summary>
public sealed class TakeStockVoidResilienceTests
{
    // Second product in the batch — no seeded stock, so its positive count mints fresh stock and
    // succeeds, carrying it into the void loop after the poison product.
    private static readonly Guid SecondProductId = Guid.Parse("22222222-0000-0000-0000-2000000000ff");

    [Fact(DisplayName = "POST Save: one product's void failure does not abandon the rest of the batch")]
    public async Task Post_Save_MidBatchVoidFailure_StillVoidsSiblings()
    {
        // Poison the FIRST product's void so VoidDeferredUnitGapLines throws for it mid-batch.
        using var factory = new TakeStockVoidFailureFactory(poisonProductId: TakeStockFixture.FlourId);
        var client = factory.CreateAuthClient(TakeStockFixture.HouseholdAId);

        var pageResp = await client.GetAsync($"/pantry/take-stock/{TakeStockFixture.PantryLocId}");
        var token = ExtractAntiforgeryToken(await pageResp.Content.ReadAsStringAsync());

        // Two dirty rows: Flour (poison — void throws) FIRST, a fresh product SECOND.
        var payload = new
        {
            items = new[]
            {
                new
                {
                    productId = TakeStockFixture.FlourId,
                    countedValue = 300m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                    reason = "Correction",
                },
                new
                {
                    productId = SecondProductId,
                    countedValue = 5m,
                    countedUnitId = TakeStockFixture.GramUnitId,
                    reason = "Correction",
                },
            },
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/pantry/take-stock/{TakeStockFixture.PantryLocId}?handler=Save")
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var resp = await client.SendAsync(request);

        // Voids are best-effort: the counts are already committed, so the poison void failure must
        // not fail the response. Both counts should report success.
        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadFromJsonAsync<SaveResponse>();
        Assert.NotNull(data);
        Assert.Equal(2, data!.Results.Count);
        Assert.All(data.Results, r => Assert.True(r.IsSuccess, $"Expected success but got: {r.Error}"));

        // Load-bearing assertion: BOTH products had their void attempted. The first product threw;
        // the second's presence proves the foreach kept going past that failure. If the try/catch
        // still wrapped the whole loop, the throw would abort before the second product was reached.
        var attempted = factory.PoisonRepo.AttemptedProductIds;
        Assert.Contains(TakeStockFixture.FlourId, attempted);
        Assert.Contains(SecondProductId, attempted);
        Assert.Equal(2, attempted.Count);
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    private sealed class SaveResponse
    {
        public List<SaveResultItem> Results { get; set; } = [];
    }

    private sealed class SaveResultItem
    {
        public Guid    ProductId { get; set; }
        public bool    IsSuccess { get; set; }
        public string? Error     { get; set; }
    }
}

/// <summary>
/// WAF factory for the void-resilience regression test. Registers the standard Take Stock fakes and
/// replaces <see cref="ICookEventRepository"/> with a <see cref="PoisonCookEventRepository"/> that
/// throws when <see cref="VoidDeferredUnitGapLines"/> queries the poison product — while recording
/// every product whose void was attempted.
/// </summary>
public sealed class TakeStockVoidFailureFactory : WebApplicationFactory<Program>
{
    private readonly Guid _poisonProductId;

    public PoisonCookEventRepository PoisonRepo { get; }

    public TakeStockVoidFailureFactory(Guid poisonProductId)
    {
        _poisonProductId = poisonProductId;
        PoisonRepo = new PoisonCookEventRepository(poisonProductId);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            TakeStockFragmentFactory.RegisterFakes(services);

            // Replace the EF-backed cook-event repository with the poison fake so the real
            // VoidDeferredUnitGapLines runs against it and throws for the poison product.
            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(PoisonRepo);
        });
    }

    public HttpClient CreateAuthClient(Guid householdId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, householdId.ToString());
        return client;
    }
}

/// <summary>
/// Fake <see cref="ICookEventRepository"/> that records every product id whose deferred-unit-gap
/// lines are queried (i.e. every product whose void is attempted) and throws for one designated
/// poison product — simulating a per-product save failure (e.g. a <c>DbUpdateConcurrencyException</c>)
/// during the void. Non-poison products return no cook events, so the void is a harmless no-op.
/// </summary>
public sealed class PoisonCookEventRepository(Guid poisonProductId) : ICookEventRepository
{
    private readonly ConcurrentQueue<Guid> _attempted = new();

    /// <summary>Every product id whose void was attempted, in order.</summary>
    public IReadOnlyList<Guid> AttemptedProductIds => _attempted.ToArray();

    public Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        foreach (var id in productIds)
        {
            _attempted.Enqueue(id);
            if (id == poisonProductId)
                throw new InvalidOperationException(
                    "Simulated per-product void failure (e.g. DbUpdateConcurrencyException).");
        }

        return Task.FromResult<IReadOnlyList<CookEvent>>([]);
    }

    public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);

    public Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>([]);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
