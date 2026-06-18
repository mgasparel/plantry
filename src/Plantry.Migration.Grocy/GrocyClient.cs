using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper for the Grocy REST API.
/// Credentials are supplied via <see cref="GrocyOptions"/> (user secrets in dev, env vars in prod Docker).
/// All methods return the raw DTO collections; no domain logic lives here.
/// </summary>
public sealed class GrocyClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public GrocyClient(HttpClient http, IOptions<GrocyOptions> options)
    {
        _http = http;
        var opts = options.Value;

        if (!string.IsNullOrWhiteSpace(opts.Url))
            _http.BaseAddress = new Uri(opts.Url.TrimEnd('/') + "/");

        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            _http.DefaultRequestHeaders.Add("GROCY-API-KEY", opts.ApiKey);

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<IReadOnlyList<GrocyProduct>> GetProductsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyProduct>("products", ct);

    public Task<IReadOnlyList<GrocyQuantityUnit>> GetQuantityUnitsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyQuantityUnit>("quantity_units", ct);

    public Task<IReadOnlyList<GrocyQuantityUnitConversion>> GetQuantityUnitConversionsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyQuantityUnitConversion>("quantity_unit_conversions", ct);

    public Task<IReadOnlyList<GrocyLocation>> GetLocationsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyLocation>("locations", ct);

    public Task<IReadOnlyList<GrocyProductGroup>> GetProductGroupsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyProductGroup>("product_groups", ct);

    public Task<IReadOnlyList<GrocyRecipe>> GetRecipesAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyRecipe>("recipes", ct);

    public Task<IReadOnlyList<GrocyRecipePosition>> GetRecipePositionsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyRecipePosition>("recipes_pos", ct);

    public Task<IReadOnlyList<GrocyRecipeNesting>> GetRecipeNestingsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyRecipeNesting>("recipes_nestings", ct);

    public Task<IReadOnlyList<GrocyUserfield>> GetUserfieldsAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyUserfield>("userfields", ct);

    public Task<IReadOnlyList<GrocyProductBarcode>> GetProductBarcodesAsync(CancellationToken ct = default)
        => GetObjectsAsync<GrocyProductBarcode>("product_barcodes", ct);

    private async Task<IReadOnlyList<T>> GetObjectsAsync<T>(string objectName, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/objects/{objectName}", ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync<List<T>>(content, JsonOptions, ct);
        return result ?? [];
    }
}
