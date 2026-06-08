using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

public sealed class UnitCommandsTests
{
    [Fact]
    public async Task CreateUnitCommand_Adds_Unit_For_Current_Household()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeUnitRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new CreateUnitCommand("g", "Gram", Dimension.Mass, 1m, isBase: true, repo, tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var unit = Assert.Single(repo.Items);
        Assert.Equal(result.Value, unit.Id);
        Assert.Equal(householdId, unit.HouseholdId.Value);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task CreateUnitCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeUnitRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new CreateUnitCommand("g", "Gram", Dimension.Mass, 1m, isBase: false, repo, tenant)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task CreateUnitCommand_Fails_On_Duplicate_Code()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeUnitRepository();
        var tenant = new FakeTenantContext(householdId);
        repo.Items.Add(CatalogUnit.Create(Plantry.SharedKernel.HouseholdId.From(householdId), "g", "Gram", Dimension.Mass, 1m, isBase: true));

        var result = await new CreateUnitCommand("g", "Some other gram", Dimension.Mass, 1m, isBase: false, repo, tenant)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateUnitCode", result.Error.Code);
        Assert.Single(repo.Items);
        Assert.Equal(0, repo.SaveChangesCalls);
    }
}
