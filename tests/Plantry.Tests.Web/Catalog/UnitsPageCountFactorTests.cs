using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Pantry.Domain.Unit;

namespace Plantry.Tests.Web.Catalog;

/// <summary>
/// L4 Web integration tests for plantry-vw6r — the Catalog &gt; Units "Add a custom unit" form must
/// reject a non-1 Factor on a Count-dimension unit both client-side (the Alpine guard disables the
/// Factor field and forces it to 1) and server-side (<c>CreateUnitCommand</c> is the real gate; the
/// client-side half alone is bypassable via a raw POST).
///
/// Two things are pinned here:
/// 1. The client-side guard's `@change` comparison token actually matches the value the browser sees
///    for the Count `&lt;option&gt;` — `Html.GetEnumSelectList&lt;Dimension&gt;()` renders the enum's
///    numeric value, not its name, so the test captures the real rendered value rather than assuming
///    a specific number.
/// 2. A raw POST with a non-1 factor on a Count unit is rejected server-side and the rejection message
///    is surfaced back to the user on the re-rendered page (not merely returned from the command).
/// </summary>
public sealed class UnitsPageCountFactorTests : IDisposable
{
    private readonly UnitsPageFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private async Task<(string Token, string Html)> GetUnitsPageAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Catalog/Units");
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Units page.");
        return (match.Groups[1].Value, html);
    }

    [Fact(DisplayName = "Units page — Dimension select's Count option value matches the Alpine change-handler comparison token")]
    public async Task DimensionSelect_ChangeHandler_ComparesAgainstRealCountOptionValue()
    {
        var client = AuthClient();
        var (_, html) = await GetUnitsPageAsync(client);

        var selectBlock = Regex.Match(
            html, "<select[^>]*name=\"Input\\.Dimension\"[^>]*>.*?</select>", RegexOptions.Singleline);
        Assert.True(selectBlock.Success, "Dimension <select> not found on the Units page.");

        var openTagEnd = selectBlock.Value.IndexOf('>');
        var openTag = selectBlock.Value[..(openTagEnd + 1)];

        var countOption = Regex.Match(selectBlock.Value, "<option value=\"([^\"]+)\"[^>]*>Count</option>");
        Assert.True(countOption.Success, "Count <option> not found in the Dimension select.");
        var countValue = countOption.Groups[1].Value;

        // Pins the invariant: the @change handler compares against the value the browser actually
        // sends for Count, not a hardcoded guess (e.g. the enum name or an assumed ordinal).
        Assert.Contains($"=== '{countValue}'", openTag);

        // Fresh GET defaults to Mass, so the Factor field must start enabled — @change never
        // fires on load, so the initial isCount state comes entirely from the x-data initializer.
        Assert.Contains("isCount: false", html);

        // The isCount state is inert unless the Factor field actually consumes it — pin that the
        // field is wired to it, not merely that isCount exists somewhere on the page.
        var factorInput = Regex.Match(html, "<input[^>]*name=\"Input\\.FactorToBase\"[^>]*>");
        Assert.True(factorInput.Success, "Factor input not found on the Units page.");
        Assert.Contains("x-bind:disabled=\"isCount\"", factorInput.Value);
        Assert.Contains("x-bind:value=\"isCount ? 1 : $el.value\"", factorInput.Value);

        // The hint span is the only user-facing explanation of WHY the Factor field greys out on
        // the client path (the server message only appears on a raw-POST bypass) — pin that it is
        // actually wired to isCount and carries the expected text, not merely present somewhere.
        Assert.Matches(
            new Regex("<span[^>]*class=\"field__hint\"[^>]*x-show=\"isCount\"[^>]*>Count units always have a factor of 1"),
            html);
    }

    [Fact(DisplayName = "Create unit — Count dimension with non-1 factor is rejected server-side and the message is rendered")]
    public async Task CreateUnit_CountWithNonOneFactor_RejectsAndRendersMessage()
    {
        var client = AuthClient();
        var (token, html) = await GetUnitsPageAsync(client);

        var countOption = Regex.Match(html, "<option value=\"([^\"]+)\"[^>]*>Count</option>");
        Assert.True(countOption.Success, "Count <option> not found in the Dimension select.");
        var countValue = countOption.Groups[1].Value;

        var response = await client.PostAsync(
            "/Catalog/Units?handler=Create",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("Input.Code", "dz"),
                new KeyValuePair<string, string>("Input.Name", "Dozen"),
                new KeyValuePair<string, string>("Input.Dimension", countValue),
                new KeyValuePair<string, string>("Input.FactorToBase", "12"),
            }));

        // Re-renders the page (not a redirect) because the command rejected the input.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var resultHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("Count units must have a factor of 1", resultHtml);

        // The re-rendered form must boot with the Factor field already disabled — @change does
        // not fire on load, so this is the only thing that protects the user from re-submitting
        // the same rejected value.
        Assert.Contains("isCount: true", resultHtml);

        // No unit was persisted.
        Assert.Empty(_factory.UnitRepo.Items);
        Assert.Equal(0, _factory.UnitRepo.SaveChangesCalls);
    }

    [Fact(DisplayName = "Create unit — Count dimension with factor 1 is accepted")]
    public async Task CreateUnit_CountWithFactorOne_Succeeds()
    {
        var client = AuthClient();
        var (token, html) = await GetUnitsPageAsync(client);

        var countOption = Regex.Match(html, "<option value=\"([^\"]+)\"[^>]*>Count</option>");
        Assert.True(countOption.Success, "Count <option> not found in the Dimension select.");
        var countValue = countOption.Groups[1].Value;

        var response = await client.PostAsync(
            "/Catalog/Units?handler=Create",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("Input.Code", "bunch"),
                new KeyValuePair<string, string>("Input.Name", "Bunch"),
                new KeyValuePair<string, string>("Input.Dimension", countValue),
                new KeyValuePair<string, string>("Input.FactorToBase", "1"),
            }));

        // Redirects back to the Units page on success.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var unit = Assert.Single(_factory.UnitRepo.Items);
        Assert.Equal("bunch", unit.Code);
        Assert.Equal(1m, unit.FactorToBase);
    }

    [Fact(DisplayName = "Create unit — Count dimension with the Factor field disabled by the client guard defaults to 1")]
    public async Task CreateUnit_CountWithFactorFieldDisabledByClientGuard_DefaultsToOne()
    {
        // A `disabled` input posts no value at all — this is exactly what the browser sends once
        // the client-side guard (x-bind:disabled="isCount") takes over. The server must not rely
        // on the client having posted anything; it relies on InputModel.FactorToBase's `= 1m`
        // property initializer (Index.cshtml.cs) surviving model binding when the key is absent.
        // This test pins that initializer as load-bearing for the client guard's primary path.
        var client = AuthClient();
        var (token, html) = await GetUnitsPageAsync(client);

        var countOption = Regex.Match(html, "<option value=\"([^\"]+)\"[^>]*>Count</option>");
        Assert.True(countOption.Success, "Count <option> not found in the Dimension select.");
        var countValue = countOption.Groups[1].Value;

        var response = await client.PostAsync(
            "/Catalog/Units?handler=Create",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("Input.Code", "doz"),
                new KeyValuePair<string, string>("Input.Name", "Dozen"),
                new KeyValuePair<string, string>("Input.Dimension", countValue),
                // Input.FactorToBase intentionally omitted — the disabled input sends no value.
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var unit = Assert.Single(_factory.UnitRepo.Items);
        Assert.Equal("doz", unit.Code);
        Assert.Equal(1m, unit.FactorToBase);
    }
}

// ── WAF factory ───────────────────────────────────────────────────────────────

/// <summary>
/// L4 <see cref="WebApplicationFactory{TEntryPoint}"/> for the Catalog &gt; Units page. Replaces
/// <see cref="IUnitRepository"/> with an in-memory fake; no EF / Postgres touched for the unit
/// creation seam under test.
/// </summary>
internal sealed class UnitsPageFactory : WebApplicationFactory<Program>
{
    internal FakeCountFactorUnitRepository UnitRepo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(UnitRepo);
        });
    }
}

// ── Fake implementation ─────────────────────────────────────────────────────────

internal sealed class FakeCountFactorUnitRepository : IUnitRepository
{
    internal List<CatalogUnit> Items { get; } = [];
    internal int SaveChangesCalls { get; private set; }

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) => Task.FromResult(Items.ToList());

    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default)
    {
        Items.Add(unit);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
