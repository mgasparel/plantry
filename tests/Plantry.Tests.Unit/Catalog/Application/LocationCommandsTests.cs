using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Application;

public sealed class LocationCommandsTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public async Task CreateLocationCommand_Adds_Location_For_Current_Household()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeLocationRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new CreateLocationCommand("Garage freezer", LocationType.Frozen, repo, tenant).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var location = Assert.Single(repo.Items);
        Assert.Equal(result.Value, location.Id);
        Assert.Equal(householdId, location.HouseholdId.Value);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task CreateLocationCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeLocationRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new CreateLocationCommand("Pantry", LocationType.Ambient, repo, tenant).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task CreateLocationCommand_Fails_On_Duplicate_Name()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeLocationRepository();
        var tenant = new FakeTenantContext(householdId);
        repo.Items.Add(Location.Create(Plantry.SharedKernel.HouseholdId.From(householdId), "Pantry", LocationType.Ambient));

        var result = await new CreateLocationCommand("Pantry", LocationType.Frozen, repo, tenant).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateLocationName", result.Error.Code);
        Assert.Single(repo.Items);
    }

    [Fact]
    public async Task UpdateLocationCommand_Renames_Existing_Location()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeLocationRepository();
        var location = Location.Create(householdId, "Pantry", LocationType.Ambient);
        repo.Items.Add(location);

        var result = await new UpdateLocationCommand(location.Id, "Kitchen pantry", repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Kitchen pantry", location.Name);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task UpdateLocationCommand_Fails_When_Location_Not_Found()
    {
        var repo = new FakeLocationRepository();

        var result = await new UpdateLocationCommand(LocationId.New(), "Pantry", repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task UpdateLocationCommand_Fails_When_Renaming_To_Another_Locations_Name()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeLocationRepository();
        var pantry = Location.Create(householdId, "Pantry", LocationType.Ambient);
        var freezer = Location.Create(householdId, "Freezer", LocationType.Frozen);
        repo.Items.Add(pantry);
        repo.Items.Add(freezer);

        var result = await new UpdateLocationCommand(freezer.Id, "Pantry", repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateLocationName", result.Error.Code);
        Assert.Equal("Freezer", freezer.Name);
    }

    [Fact]
    public async Task ArchiveLocationCommand_Soft_Deletes_Location_Keeping_It_Resolvable()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeLocationRepository();
        var location = Location.Create(householdId, "Pantry", LocationType.Ambient);
        repo.Items.Add(location);

        var result = await new ArchiveLocationCommand(location.Id, repo, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(location.IsArchived);
        // The row stays (a referencing product can still resolve its name) but drops out of the active list.
        var stillPresent = Assert.Single(repo.Items);
        Assert.Same(location, stillPresent);
        Assert.Empty(await repo.ListActiveAsync());
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ArchiveLocationCommand_Fails_When_Location_Not_Found()
    {
        var repo = new FakeLocationRepository();

        var result = await new ArchiveLocationCommand(LocationId.New(), repo, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task UnarchiveLocationCommand_Restores_Archived_Location()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeLocationRepository();
        var location = Location.Create(householdId, "Pantry", LocationType.Ambient);
        location.Archive(Clock);
        repo.Items.Add(location);

        var result = await new UnarchiveLocationCommand(location.Id, repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(location.IsArchived);
        Assert.Single(await repo.ListActiveAsync());
    }
}
