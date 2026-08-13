using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Catalog.Categories;

namespace Plantry.Tests.Unit.Catalog.Application;

public sealed class CategorySortTests
{
    [Theory]
    [InlineData("name", false, "Alpha", "Zeta")]
    [InlineData("name", true, "Zeta", "Alpha")]
    [InlineData("name-desc", false, "Zeta", "Alpha")]
    public async Task OnGet_sorts_categories_by_name_without_changing_manual_order(
        string sort, bool descending, string first, string last)
    {
        var repository = CreateRepository();
        var model = CreateModel(repository);

        model.Sort = sort;
        model.Desc = descending;
        await model.OnGetAsync();

        Assert.Equal(first, model.Categories[0].Name);
        Assert.Equal(last, model.Categories[1].Name);
        Assert.Equal(0, repository.Items.Single(c => c.Name == "Zeta").SortOrder);
        Assert.Equal(10, repository.Items.Single(c => c.Name == "Alpha").SortOrder);
    }

    [Fact]
    public async Task OnGet_normalizes_descending_sort_option_for_selected_ui()
    {
        var repository = CreateRepository();
        var model = CreateModel(repository);
        model.Sort = "name-desc";

        await model.OnGetAsync();

        Assert.Equal("name", model.Sort);
        Assert.True(model.Desc);
        Assert.Equal(new[] { "Zeta", "Alpha" }, model.Categories.Select(c => c.Name));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("name-desc")]
    [InlineData("expiry")]
    [InlineData("expiry-desc")]
    public async Task Reorder_rejects_every_sorted_request_before_persisting(string sort)
    {
        var repository = CreateRepository();
        var model = CreateModel(repository);
        model.Sort = sort;
        model.PageContext.HttpContext = new DefaultHttpContext();
        model.PageContext.HttpContext.Request.Query = new QueryCollection(
            new Dictionary<string, StringValues> { ["sort"] = sort });

        var result = await model.OnPostReorderAsync(repository.Items.Select(c => c.Id.Value).ToList());

        Assert.IsType<ConflictResult>(result);
        Assert.Equal(0, repository.SaveChangesCalls);
        Assert.Equal(new[] { 10, 0 }, repository.Items.OrderBy(c => c.Name).Select(c => c.SortOrder));
    }

    [Fact]
    public void IsSortedRequest_accepts_canonical_and_descending_query_forms()
    {
        Assert.All(new[] { "name", "name-desc", "expiry", "expiry-desc" },
            sort => Assert.True(IndexModel.IsSortedRequest(sort)));
        Assert.False(IndexModel.IsSortedRequest(null));
        Assert.False(IndexModel.IsSortedRequest("manual"));
    }

    private static FakeCategoryRepository CreateRepository()
    {
        var household = HouseholdId.New();
        var repository = new FakeCategoryRepository();
        repository.Items.Add(Category.Create(household, "Zeta", sortOrder: 0));
        repository.Items.Add(Category.Create(household, "Alpha", sortOrder: 10));
        return repository;
    }

    private static IndexModel CreateModel(FakeCategoryRepository repository) => new(
        repository,
        new FakeTenantContext(Guid.NewGuid()),
        SystemClock.Instance,
        NullLogger<CreateCategoryCommand>.Instance,
        NullLogger<UpdateCategoryCommand>.Instance);
}
