using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Catalog;

public sealed class CategoriesPageSortingTests : IDisposable
{
    private static readonly Guid HouseholdId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private readonly CategoriesPageFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("name", "Alpha", "Zeta", "Name (A–Z)")]
    [InlineData("name-desc", "Zeta", "Alpha", "Name (Z–A)")]
    public async Task Get_sorted_name_view_renders_order_selected_option_and_read_only_hint(
        string sort, string first, string second, string selectedLabel)
    {
        var response = await Client().GetAsync($"/Catalog/Categories?sort={sort}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(html.IndexOf($">{first}<", StringComparison.Ordinal) < html.IndexOf($">{second}<", StringComparison.Ordinal));
        Assert.Contains($"<option value=\"{sort}\" selected", html);
        Assert.Contains(selectedLabel, html);
        Assert.Contains("Drag reordering is disabled in sorted views", html);
        Assert.DoesNotContain("data-reorder-url", html);
        Assert.Equal(new[] { 10, 0 }, _factory.CategoryRepo.Items.OrderBy(c => c.Name).Select(c => c.SortOrder));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("name-desc")]
    [InlineData("expiry")]
    [InlineData("expiry-desc")]
    public async Task Post_reorder_with_sorted_query_is_rejected_without_mutation(string sort)
    {
        var client = Client();
        var html = await (await client.GetAsync("/Catalog/Categories")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var ids = _factory.CategoryRepo.Items.Select(c => c.Id.Value.ToString()).ToArray();

        var response = await client.PostAsync(
            $"/Catalog/Categories?handler=Reorder&sort={sort}",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("ids", ids[1]),
                new KeyValuePair<string, string>("ids", ids[0]),
            }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, _factory.CategoryRepo.SaveChangesCalls);
        Assert.Equal(new[] { 10, 0 }, _factory.CategoryRepo.Items.OrderBy(c => c.Name).Select(c => c.SortOrder));
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }
}

internal sealed class CategoriesPageFactory : WebApplicationFactory<Program>
{
    internal FakeCategoriesPageRepository CategoryRepo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.RemoveAll<ICategoryRepository>();
            services.AddSingleton<ICategoryRepository>(CategoryRepo);
        });
    }
}

internal sealed class FakeCategoriesPageRepository : ICategoryRepository
{
    internal List<Category> Items { get; } =
    [
        Category.Create(HouseholdId.From(CategoriesPageSortingTestsHousehold.Value), "Zeta", sortOrder: 0),
        Category.Create(HouseholdId.From(CategoriesPageSortingTestsHousehold.Value), "Alpha", sortOrder: 10),
    ];
    internal int SaveChangesCalls { get; private set; }

    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) => Task.FromResult(Items.SingleOrDefault(c => c.Id == id));
    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult(Items.SingleOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
    public Task<List<Category>> ListAsync(CancellationToken ct = default) => Task.FromResult(Items.ToList());
    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(Items.Where(c => !c.IsArchived).ToList());
    public Task AddAsync(Category category, CancellationToken ct = default) { Items.Add(category); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) { SaveChangesCalls++; return Task.CompletedTask; }
}

internal static class CategoriesPageSortingTestsHousehold
{
    internal static readonly Guid Value = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
}
