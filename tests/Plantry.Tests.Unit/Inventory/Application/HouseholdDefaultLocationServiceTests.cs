using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L1 tests for <see cref="HouseholdDefaultLocationService"/> and the
/// <see cref="HouseholdInventorySettings.DefaultLocationId"/> aggregate field — the storage +
/// read-or-default seam behind the household's default storage location (plantry-iypo), the middle
/// rung in <c>InventoryProducerAdapter.ProduceAsync</c>'s yield-placement fallback chain. Mirrors
/// <see cref="ExpiringSoonSettingsServiceTests"/>'s shape for the sibling setting on the same aggregate.
/// </summary>
public sealed class HouseholdDefaultLocationServiceTests
{
    private readonly Guid _household = Guid.NewGuid();

    private static HouseholdDefaultLocationService Service(
        FakeHouseholdInventorySettingsRepository settings, FakeLocationRepository locations, Guid? household) =>
        new(settings, locations, new FakeTenantContext(household), NullLogger<HouseholdDefaultLocationService>.Instance);

    [Fact(DisplayName = "GetDefaultLocationId returns null when the household has no settings row")]
    public async Task Get_Returns_Null_When_Unset()
    {
        var result = await Service(new FakeHouseholdInventorySettingsRepository(), new FakeLocationRepository(), _household)
            .GetDefaultLocationIdAsync();
        Assert.Null(result);
    }

    [Fact(DisplayName = "GetDefaultLocationId returns null when there is no household in context")]
    public async Task Get_Returns_Null_When_No_Household()
    {
        var result = await Service(new FakeHouseholdInventorySettingsRepository(), new FakeLocationRepository(), household: null)
            .GetDefaultLocationIdAsync();
        Assert.Null(result);
    }

    [Fact(DisplayName = "SetDefaultLocation seeds a row on first write and GetDefaultLocationId reads it back")]
    public async Task Set_Creates_Row_And_Persists()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var fridge = Location.Create(HouseholdId.From(_household), "Fridge", LocationType.Ambient);
        locationsRepo.Items.Add(fridge);
        var service = Service(settingsRepo, locationsRepo, _household);

        var result = await service.SetDefaultLocationAsync(fridge.Id.Value);

        Assert.True(result.IsSuccess);
        Assert.Equal(fridge.Id, Assert.Single(settingsRepo.Items).DefaultLocationId);
        Assert.Equal(fridge.Id.Value, await service.GetDefaultLocationIdAsync());
    }

    [Fact(DisplayName = "SetDefaultLocation updates the existing row rather than adding a second")]
    public async Task Set_Updates_Existing_Row()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var fridge = Location.Create(HouseholdId.From(_household), "Fridge", LocationType.Ambient);
        var freezer = Location.Create(HouseholdId.From(_household), "Freezer", LocationType.Frozen);
        locationsRepo.Items.Add(fridge);
        locationsRepo.Items.Add(freezer);
        var service = Service(settingsRepo, locationsRepo, _household);

        await service.SetDefaultLocationAsync(fridge.Id.Value);
        await service.SetDefaultLocationAsync(freezer.Id.Value);

        Assert.Equal(freezer.Id, Assert.Single(settingsRepo.Items).DefaultLocationId);
        Assert.Equal(freezer.Id.Value, await service.GetDefaultLocationIdAsync());
    }

    [Fact(DisplayName = "SetDefaultLocation(null) clears a previously set default")]
    public async Task Set_Null_Clears_Existing_Default()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var fridge = Location.Create(HouseholdId.From(_household), "Fridge", LocationType.Ambient);
        locationsRepo.Items.Add(fridge);
        var service = Service(settingsRepo, locationsRepo, _household);
        await service.SetDefaultLocationAsync(fridge.Id.Value);

        var result = await service.SetDefaultLocationAsync(null);

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(settingsRepo.Items).DefaultLocationId);
        Assert.Null(await service.GetDefaultLocationIdAsync());
    }

    [Fact(DisplayName = "SetDefaultLocation rejects an unknown location id and writes nothing")]
    public async Task Set_Rejects_Unknown_Location()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var service = Service(settingsRepo, locationsRepo, _household);

        var result = await service.SetDefaultLocationAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Empty(settingsRepo.Items);
    }

    [Fact(DisplayName = "SetDefaultLocation rejects an archived location and writes nothing")]
    public async Task Set_Rejects_Archived_Location()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var archived = Location.Create(HouseholdId.From(_household), "Old Shelf", LocationType.Ambient);
        archived.Archive(SystemClock.Instance);
        locationsRepo.Items.Add(archived);
        var service = Service(settingsRepo, locationsRepo, _household);

        var result = await service.SetDefaultLocationAsync(archived.Id.Value);

        Assert.True(result.IsFailure);
        Assert.Empty(settingsRepo.Items);
    }

    [Fact(DisplayName = "SetDefaultLocation returns Unauthorized when there is no household in context")]
    public async Task Set_Requires_Household()
    {
        var settingsRepo = new FakeHouseholdInventorySettingsRepository();
        var locationsRepo = new FakeLocationRepository();
        var result = await Service(settingsRepo, locationsRepo, household: null).SetDefaultLocationAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Empty(settingsRepo.Items);
    }

    [Fact(DisplayName = "A freshly created settings record carries no default location")]
    public void Create_Seeds_No_Default_Location()
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(_household));
        Assert.Null(settings.DefaultLocationId);
    }

    [Fact(DisplayName = "SetDefaultLocationId on the aggregate sets and clears without validation")]
    public void SetDefaultLocationId_Sets_And_Clears()
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(_household));
        var locationId = LocationId.New();

        settings.SetDefaultLocationId(locationId);
        Assert.Equal(locationId, settings.DefaultLocationId);

        settings.SetDefaultLocationId(null);
        Assert.Null(settings.DefaultLocationId);
    }
}
