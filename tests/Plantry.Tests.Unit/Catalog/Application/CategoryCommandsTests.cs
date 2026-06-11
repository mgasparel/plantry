using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Application;

public sealed class CategoryCommandsTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public async Task CreateCategoryCommand_Adds_Category_For_Current_Household()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeCategoryRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new CreateCategoryCommand("Dairy", 7, 1, repo, tenant).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var category = Assert.Single(repo.Items);
        Assert.Equal(result.Value, category.Id);
        Assert.Equal(householdId, category.HouseholdId.Value);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task CreateCategoryCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeCategoryRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new CreateCategoryCommand("Dairy", null, 0, repo, tenant).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task CreateCategoryCommand_Fails_On_Duplicate_Name()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeCategoryRepository();
        var tenant = new FakeTenantContext(householdId);
        repo.Items.Add(Category.Create(Plantry.SharedKernel.HouseholdId.From(householdId), "Dairy"));

        var result = await new CreateCategoryCommand("Dairy", null, 0, repo, tenant).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateCategoryName", result.Error.Code);
        Assert.Single(repo.Items);
    }

    [Fact]
    public async Task UpdateCategoryCommand_Updates_Existing_Category()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var category = Category.Create(householdId, "Dairy", 7, 1);
        repo.Items.Add(category);

        var result = await new UpdateCategoryCommand(category.Id, "Dairy & Eggs", 14, 2, repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Dairy & Eggs", category.Name);
        Assert.Equal(14, category.DefaultDueDays);
        Assert.Equal(2, category.SortOrder);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task UpdateCategoryCommand_Fails_When_Category_Not_Found()
    {
        var repo = new FakeCategoryRepository();

        var result = await new UpdateCategoryCommand(CategoryId.New(), "Dairy", null, 0, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task UpdateCategoryCommand_Fails_When_Renaming_To_Another_Categorys_Name()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var dairy = Category.Create(householdId, "Dairy");
        var produce = Category.Create(householdId, "Produce");
        repo.Items.Add(dairy);
        repo.Items.Add(produce);

        var result = await new UpdateCategoryCommand(produce.Id, "Dairy", null, 0, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateCategoryName", result.Error.Code);
        Assert.Equal("Produce", produce.Name);
    }

    [Fact]
    public async Task ArchiveCategoryCommand_Soft_Deletes_Category_Keeping_It_Resolvable()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var category = Category.Create(householdId, "Dairy");
        repo.Items.Add(category);

        var result = await new ArchiveCategoryCommand(category.Id, repo, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(category.IsArchived);
        // The row stays (a referencing product can still resolve its name) but drops out of the active list.
        var stillPresent = Assert.Single(repo.Items);
        Assert.Same(category, stillPresent);
        Assert.Empty(await repo.ListActiveAsync());
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ArchiveCategoryCommand_Fails_When_Category_Not_Found()
    {
        var repo = new FakeCategoryRepository();

        var result = await new ArchiveCategoryCommand(CategoryId.New(), repo, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task UnarchiveCategoryCommand_Restores_Archived_Category()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var category = Category.Create(householdId, "Dairy");
        category.Archive(Clock);
        repo.Items.Add(category);

        var result = await new UnarchiveCategoryCommand(category.Id, repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(category.IsArchived);
        Assert.Single(await repo.ListActiveAsync());
    }

    [Fact]
    public async Task ReorderCategoriesCommand_Assigns_Sort_Orders_In_Given_Sequence()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var dairy = Category.Create(householdId, "Dairy", sortOrder: 0);
        var produce = Category.Create(householdId, "Produce", sortOrder: 10);
        var bakery = Category.Create(householdId, "Bakery", sortOrder: 20);
        repo.Items.AddRange([dairy, produce, bakery]);

        // New order: bakery, dairy, produce
        var result = await new ReorderCategoriesCommand([bakery.Id, dairy.Id, produce.Id], repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, bakery.SortOrder);
        Assert.Equal(10, dairy.SortOrder);
        Assert.Equal(20, produce.SortOrder);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ReorderCategoriesCommand_Skips_Unknown_Ids()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var dairy = Category.Create(householdId, "Dairy", sortOrder: 0);
        repo.Items.Add(dairy);

        var result = await new ReorderCategoriesCommand([CategoryId.New(), dairy.Id], repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, dairy.SortOrder);
    }

    [Fact]
    public async Task UpdateCategoryCommand_Allows_Rename_To_Same_Name()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeCategoryRepository();
        var category = Category.Create(householdId, "Dairy");
        repo.Items.Add(category);

        // Renaming to the current name must not be treated as a duplicate.
        var result = await new UpdateCategoryCommand(category.Id, "Dairy", null, 0, repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Dairy", category.Name);
    }
}
