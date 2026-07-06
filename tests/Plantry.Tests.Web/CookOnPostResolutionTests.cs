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
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Handler-level tests for <c>CookModel.OnPostAsync</c> resolution-mapping logic (plantry-oej).
///
/// These tests verify that the three resolution-mapping branches in OnPostAsync correctly translate
/// the posted form inputs into the right IInventoryConsumer.ConsumeAsync calls:
/// <list type="number">
///   <item>Skip → no consume for the skipped ingredient's product (AC1).</item>
///   <item>PickerSelections with a real VariantId → consume targets the VARIANT product
///         with the scaled quantity and ingredient unit (AC2).</item>
///   <item>PickerSelections with VariantId == Guid.Empty → filtered out; ingredient falls
///         through to default auto-selection (AC3).</item>
/// </list>
///
/// Assertions capture at the IInventoryConsumer seam over a real CookRecipe — no ICookRecipe
/// interface is introduced (design note in plantry-oej).
/// </summary>
public sealed class CookOnPostResolutionTests : IDisposable
{
    private readonly CookPostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // The fixture recipe is 4 servings; POSTing Servings=4 means scale=1 so quantities are unchanged.
    private const int PostedServings = 4;

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private HttpClient AuthenticatedClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            CookConfirmFixture.HouseholdAId.ToString());
        return client;
    }

    private string CookUrl => $"/Recipes/{_factory.RecipeId}/Cook";

    /// <summary>
    /// GET the Cook page to harvest a paired antiforgery token + cookie before POSTing.
    /// Razor Pages validates the token on every POST.
    /// </summary>
    private async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync($"{CookUrl}?Servings={PostedServings}"))
            .Content.ReadAsStringAsync();

        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Cook page.");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// POST to OnPostAsync with the given form fields and return the response.
    /// Always includes the antiforgery token (harvested via a prior GET).
    /// </summary>
    private async Task<HttpResponseMessage> PostCookAsync(
        HttpClient client, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await GetAntiforgeryTokenAsync(client);

        var allFields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("Id", _factory.RecipeId.ToString()),
            new("Servings", PostedServings.ToString()),
        };
        allFields.AddRange(fields);

        return await client.PostAsync(CookUrl, new FormUrlEncodedContent(allFields));
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC1: A skipped ingredient must NOT produce a ConsumeAsync call for its product.
    ///
    /// We skip Pasta (a tracked leaf with 400g at scale=1 → would normally produce a consume of
    /// PastaId, 400g, GramUnitId). After the POST the recording consumer must have no call
    /// whose ProductId is PastaId.
    /// </summary>
    [Fact]
    public async Task Skip_ingredient_produces_no_consume_call()
    {
        var client = AuthenticatedClient();

        // Look up Pasta's IngredientId from the live recipe object.
        var pastaIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.PastaId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new("SkippedIngredientIds[0]", pastaIngredientId.ToString()),
        ]);

        // OnPostAsync redirects to the Detail page on a successful cook.
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        // Pasta must NOT appear in any consume call.
        var calls = _factory.Consumer.Calls;
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.PastaId);
    }

    /// <summary>
    /// AC2: A PickerSelections entry with a non-empty VariantId must produce a ConsumeAsync call
    /// targeting the VARIANT product with the scaled quantity and the ingredient's unit.
    ///
    /// Garlic (parent, 3 ea at scale=1) with PickerSelections[0]={GarlicParentIngredientId,
    /// GarlicFreshId} must produce a consume of (GarlicFreshId, 3m, EachUnitId) — NOT GarlicParentId.
    /// </summary>
    [Fact]
    public async Task Picker_selection_with_variant_id_consumes_variant_product()
    {
        var client = AuthenticatedClient();

        var garlicIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.GarlicParentId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", garlicIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookConfirmFixture.GarlicFreshId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        // The consume must target GarlicFreshId — not the parent.
        Assert.Contains(calls, c => c.ProductId == CookConfirmFixture.GarlicFreshId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.GarlicParentId);

        // Verify quantity (3 ea at scale=1) and unit.
        var variantCall = calls.Single(c => c.ProductId == CookConfirmFixture.GarlicFreshId);
        Assert.Equal(3m, variantCall.Quantity);
        Assert.Equal(CookConfirmFixture.EachUnitId, variantCall.UnitId);
    }

    /// <summary>
    /// AC3: A PickerSelections entry with VariantId == Guid.Empty is filtered out by the
    /// <c>pickerIndex</c> build step (the .Where(p => p.VariantId != Guid.Empty) guard in
    /// OnPostAsync:Cook.cshtml.cs:287). The ingredient falls through to default auto-selection:
    /// a consume of (GarlicParentId, 3m, EachUnitId) — as if no picker entry were posted at all.
    ///
    /// Note: GarlicParentId has TrackStock=true in the fixture catalog, but CookRecipe's
    /// default-auto-selection path uses the ingredient's OWN productId (the parent) directly,
    /// which is marked TrackStock=true, so a consume is expected.
    /// </summary>
    [Fact]
    public async Task Picker_selection_with_empty_variant_id_falls_through_to_auto_selection()
    {
        var client = AuthenticatedClient();

        var garlicIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.GarlicParentId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", garlicIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    Guid.Empty.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        // The empty VariantId entry must have been filtered; the ingredient falls to default
        // auto-selection which targets the parent product directly.
        Assert.Contains(calls, c => c.ProductId == CookConfirmFixture.GarlicParentId);
        // The variant products must NOT have been targeted by the picker path.
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.GarlicFreshId);
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.GarlicGranuleId);
    }

    /// <summary>
    /// AC4 (C9 modify): A QuantityOverrides entry for a leaf ingredient overrides the scaled
    /// quantity in the consume call.
    ///
    /// Pasta (leaf, 400g at scale=1). POST QuantityOverrides[pastaIngredientId]=250 →
    /// the consume must be (PastaId, 250m, GramUnitId) not 400m.
    /// </summary>
    [Fact]
    public async Task Leaf_quantity_override_changes_consume_quantity()
    {
        var client = AuthenticatedClient();

        var pastaIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.PastaId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new($"QuantityOverrides[{pastaIngredientId}]", "250"),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        // Pasta must be consumed at the overridden quantity, not the scaled default.
        var pastaCall = calls.Single(c => c.ProductId == CookConfirmFixture.PastaId);
        Assert.Equal(250m, pastaCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, pastaCall.UnitId);
    }

    /// <summary>
    /// AC5 (C9 modify): A PickerSelections entry combined with a QuantityOverrides entry
    /// produces a consume targeting the chosen variant at the overridden quantity.
    ///
    /// Garlic (parent, 3 ea at scale=1) with PickerSelections[0]={GarlicParentIngredientId,
    /// GarlicFreshId} and QuantityOverrides[garlicIngredientId]=5 → consume (GarlicFreshId,
    /// 5m, EachUnitId).
    /// </summary>
    [Fact]
    public async Task Picker_plus_override_consumes_variant_at_overridden_quantity()
    {
        var client = AuthenticatedClient();

        var garlicIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.GarlicParentId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new("PickerSelections[0].IngredientId", garlicIngredientId.ToString()),
            new("PickerSelections[0].VariantId",    CookConfirmFixture.GarlicFreshId.ToString()),
            new($"QuantityOverrides[{garlicIngredientId}]", "5"),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        var variantCall = calls.Single(c => c.ProductId == CookConfirmFixture.GarlicFreshId);
        Assert.Equal(5m, variantCall.Quantity);
        Assert.Equal(CookConfirmFixture.EachUnitId, variantCall.UnitId);
    }

    /// <summary>
    /// AC6 (C9 modify / regression): When no QuantityOverrides are posted, a leaf ingredient
    /// continues to use the scaled quantity (regression guard).
    ///
    /// Pasta (leaf, 400g at scale=1) with no override → consume (PastaId, 400m, GramUnitId).
    /// </summary>
    [Fact]
    public async Task No_override_leaf_uses_scaled_quantity()
    {
        var client = AuthenticatedClient();

        // Post nothing — no skips, no overrides.
        var response = await PostCookAsync(client, []);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;

        // Pasta must be consumed at the scaled default (400g, scale=1).
        var pastaCall = calls.Single(c => c.ProductId == CookConfirmFixture.PastaId);
        Assert.Equal(400m, pastaCall.Quantity);
        Assert.Equal(CookConfirmFixture.GramUnitId, pastaCall.UnitId);
    }

    /// <summary>
    /// AC (plantry-7zjm) AddedLines binding: posting an existing catalog product as an AddedLines row
    /// produces a ConsumeAsync call targeting that product at the entered quantity + unit — the added
    /// line flows through the real CookRecipe consume path. Uses Garlic, Fresh (a tracked catalog
    /// product not directly a recipe ingredient) as the added product.
    /// </summary>
    [Fact]
    public async Task Added_line_consumes_the_added_product_at_entered_quantity_and_unit()
    {
        var client = AuthenticatedClient();

        var response = await PostCookAsync(client,
        [
            new("AddedLines[0].ProductId", CookConfirmFixture.GarlicFreshId.ToString()),
            new("AddedLines[0].Quantity",  "2"),
            new("AddedLines[0].UnitId",    CookConfirmFixture.EachUnitId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var addedCall = _factory.Consumer.Calls.Single(c => c.ProductId == CookConfirmFixture.GarlicFreshId);
        Assert.Equal(2m, addedCall.Quantity);
        Assert.Equal(CookConfirmFixture.EachUnitId, addedCall.UnitId);
    }

    /// <summary>
    /// AC2 (plantry-7zjm) substitution at the Web seam: skipping a recipe ingredient and adding a
    /// different product in the same POST composes an on-the-fly substitution — the skipped product is
    /// not consumed and the added product is.
    /// </summary>
    [Fact]
    public async Task Skip_plus_added_line_composes_substitution()
    {
        var client = AuthenticatedClient();

        var pastaIngredientId = _factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookConfirmFixture.PastaId)
            .Id.Value;

        var response = await PostCookAsync(client,
        [
            new("SkippedIngredientIds[0]", pastaIngredientId.ToString()),
            new("AddedLines[0].ProductId", CookConfirmFixture.GarlicFreshId.ToString()),
            new("AddedLines[0].Quantity",  "1"),
            new("AddedLines[0].UnitId",    CookConfirmFixture.EachUnitId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var calls = _factory.Consumer.Calls;
        // Original (skipped) product is not consumed; the replacement is.
        Assert.DoesNotContain(calls, c => c.ProductId == CookConfirmFixture.PastaId);
        Assert.Contains(calls, c => c.ProductId == CookConfirmFixture.GarlicFreshId);
    }

    /// <summary>
    /// A blank/partial AddedLines row (empty product id) is filtered by OnPostAsync and never reaches
    /// the consumer — guards the model-binding edge where Alpine emits an unfilled row.
    /// </summary>
    [Fact]
    public async Task Added_line_with_empty_product_is_ignored()
    {
        var client = AuthenticatedClient();

        var before = _factory.Consumer.Calls.Count;

        var response = await PostCookAsync(client,
        [
            new("AddedLines[0].ProductId", Guid.Empty.ToString()),
            new("AddedLines[0].Quantity",  "3"),
            new("AddedLines[0].UnitId",    CookConfirmFixture.EachUnitId.ToString()),
        ]);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        // No consume call carries the empty sentinel product id.
        Assert.DoesNotContain(_factory.Consumer.Calls, c => c.ProductId == Guid.Empty);
        // The recipe's own ingredients still cooked (the empty added row did not abort the cook).
        Assert.True(_factory.Consumer.Calls.Count > before);
    }
}

// ── WAF factory for OnPostAsync resolution-mapping tests ─────────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the Cook POST resolution-mapping tests.
/// Wires a <see cref="RecordingFakeCookInventoryConsumer"/> so tests can inspect which products
/// were consumed without touching the database.
///
/// Inherits the same seam replacements as <see cref="CookConfirmFragmentFactory"/> (recipe,
/// catalog, stock, unit converter, cook events, tag and price stubs) and additionally exposes
/// the recording consumer via the <see cref="Consumer"/> property.
/// </summary>
internal sealed class CookPostFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookConfirmFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    /// <summary>
    /// The recording consumer registered with the DI container.
    /// Inspect <see cref="RecordingFakeCookInventoryConsumer.Calls"/> after each POST.
    /// </summary>
    public RecordingFakeCookInventoryConsumer Consumer { get; } = new();

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
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookConfirmFixture.Products(), CookConfirmFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookConfirmFixture.Stock()));

            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());

            // Recording consumer — the same instance is shared across requests so the test
            // can inspect calls after the POST completes.
            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(Consumer);

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
