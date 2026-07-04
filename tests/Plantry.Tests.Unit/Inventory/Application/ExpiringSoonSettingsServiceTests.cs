using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Inventory.Application;

/// <summary>
/// L1 tests for <see cref="ExpiringSoonSettingsService"/> and the
/// <see cref="HouseholdInventorySettings"/> aggregate — the storage + read-or-default seam behind
/// the per-household "expiring soon" horizon (plantry-5yhd).
/// </summary>
public sealed class ExpiringSoonSettingsServiceTests
{
    private readonly Guid _household = Guid.NewGuid();

    private ExpiringSoonSettingsService Service(FakeHouseholdInventorySettingsRepository repo, Guid? household) =>
        new(repo, new FakeTenantContext(household), NullLogger<ExpiringSoonSettingsService>.Instance);

    [Fact(DisplayName = "GetDays returns the default (7) when the household has no settings row")]
    public async Task GetDays_Defaults_When_Unset()
    {
        var days = await Service(new FakeHouseholdInventorySettingsRepository(), _household).GetDaysAsync();
        Assert.Equal(HouseholdInventorySettings.DefaultExpiringSoonDays, days);
        Assert.Equal(7, days);
    }

    [Fact(DisplayName = "GetDays returns the default when there is no household in context")]
    public async Task GetDays_Defaults_When_No_Household()
    {
        var days = await Service(new FakeHouseholdInventorySettingsRepository(), household: null).GetDaysAsync();
        Assert.Equal(HouseholdInventorySettings.DefaultExpiringSoonDays, days);
    }

    [Fact(DisplayName = "SetDays seeds a row on first write and GetDays reads it back")]
    public async Task SetDays_Creates_Row_And_Persists()
    {
        var repo = new FakeHouseholdInventorySettingsRepository();
        var service = Service(repo, _household);

        var result = await service.SetDaysAsync(14);

        Assert.True(result.IsSuccess);
        Assert.Equal(14, Assert.Single(repo.Items).ExpiringSoonDays);
        Assert.Equal(14, await service.GetDaysAsync());
    }

    [Fact(DisplayName = "SetDays updates the existing row rather than adding a second")]
    public async Task SetDays_Updates_Existing_Row()
    {
        var repo = new FakeHouseholdInventorySettingsRepository();
        var service = Service(repo, _household);

        await service.SetDaysAsync(10);
        await service.SetDaysAsync(4);

        Assert.Equal(4, Assert.Single(repo.Items).ExpiringSoonDays);
        Assert.Equal(4, await service.GetDaysAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public async Task SetDays_Rejects_Out_Of_Range(int days)
    {
        var repo = new FakeHouseholdInventorySettingsRepository();
        var result = await Service(repo, _household).SetDaysAsync(days);

        Assert.True(result.IsFailure);
        Assert.Empty(repo.Items);
    }

    [Fact(DisplayName = "SetDays returns Unauthorized when there is no household in context")]
    public async Task SetDays_Requires_Household()
    {
        var repo = new FakeHouseholdInventorySettingsRepository();
        var result = await Service(repo, household: null).SetDaysAsync(7);

        Assert.True(result.IsFailure);
        Assert.Empty(repo.Items);
    }

    [Theory]
    [InlineData(HouseholdInventorySettings.MinExpiringSoonDays)]
    [InlineData(HouseholdInventorySettings.MaxExpiringSoonDays)]
    public void SetExpiringSoonDays_Accepts_Range_Bounds(int days)
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(_household));
        settings.SetExpiringSoonDays(days);
        Assert.Equal(days, settings.ExpiringSoonDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HouseholdInventorySettings.MaxExpiringSoonDays + 1)]
    public void SetExpiringSoonDays_Rejects_Out_Of_Range(int days)
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(_household));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SetExpiringSoonDays(days));
    }

    [Fact(DisplayName = "A freshly created settings record carries the default horizon")]
    public void Create_Seeds_Default_Horizon()
    {
        var settings = HouseholdInventorySettings.Create(HouseholdId.From(_household));
        Assert.Equal(HouseholdInventorySettings.DefaultExpiringSoonDays, settings.ExpiringSoonDays);
    }
}
