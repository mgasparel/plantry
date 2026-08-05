using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class CreateSubstitutionTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid TargetProduct = Guid.NewGuid();
    private static readonly Guid TargetUnit = Guid.NewGuid();
    private static readonly Guid SubstituteProduct = Guid.NewGuid();
    private static readonly Guid SubstituteUnit = Guid.NewGuid();

    private static (CreateSubstitution Service, FakeSubstitutionRepository Substitutions) Build(Guid? household = null)
    {
        var substitutions = new FakeSubstitutionRepository();
        var tenant = new FakeTenantContext(household ?? Household);
        var service = new CreateSubstitution(substitutions, tenant, Clock, NullLogger<CreateSubstitution>.Instance);
        return (service, substitutions);
    }

    private static CreateSubstitutionCommand Command(
        decimal targetQuantity = 400m, decimal substituteQuantity = 154m,
        Guid? targetProduct = null, Guid? substituteProduct = null) =>
        new(
            targetProduct ?? TargetProduct, targetQuantity, TargetUnit,
            substituteProduct ?? SubstituteProduct, substituteQuantity, SubstituteUnit);

    [Fact(DisplayName = "Creates a new edge when no edge exists for the directed pair")]
    public async Task Create_New_Pair_Adds_Edge()
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value); // inserted a fresh edge, not a replace
        var edge = Assert.Single(substitutions.Items);
        Assert.Equal(TargetProduct, edge.TargetProductId);
        Assert.Equal(400m, edge.TargetQuantity);
        Assert.Equal(SubstituteProduct, edge.SubstituteProductId);
        Assert.Equal(154m, edge.SubstituteQuantity);
        Assert.Equal(1, substitutions.SaveChangesCalls);
    }

    [Fact(DisplayName = "A duplicate directed pair replaces the existing edge's ratio rather than erroring")]
    public async Task Create_Duplicate_Pair_Replaces_Existing_Edge()
    {
        var (service, substitutions) = Build();
        await service.ExecuteAsync(Command(targetQuantity: 400m, substituteQuantity: 154m));

        var result = await service.ExecuteAsync(Command(targetQuantity: 500m, substituteQuantity: 200m));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value); // replaced the existing edge, not an insert
        var edge = Assert.Single(substitutions.Items);
        Assert.Equal(500m, edge.TargetQuantity);
        Assert.Equal(200m, edge.SubstituteQuantity);
    }

    [Fact(DisplayName = "The reverse directed pair (B->A) is a distinct edge from A->B, not a duplicate")]
    public async Task Create_Reverse_Pair_Is_A_Distinct_Edge()
    {
        var (service, substitutions) = Build();
        await service.ExecuteAsync(Command()); // Substitute -> Target

        var reversed = new CreateSubstitutionCommand(
            SubstituteProduct, 260m, SubstituteUnit, // now target
            TargetProduct, 100m, TargetUnit); // now substitute
        var result = await service.ExecuteAsync(reversed);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, substitutions.Items.Count);
    }

    [Fact(DisplayName = "Rejects a missing target product id")]
    public async Task Create_Rejects_Missing_TargetProduct()
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command(targetProduct: Guid.Empty));

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidProduct", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Fact(DisplayName = "Rejects a missing substitute product id")]
    public async Task Create_Rejects_Missing_SubstituteProduct()
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command(substituteProduct: Guid.Empty));

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidProduct", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Fact(DisplayName = "Rejects a missing target unit id")]
    public async Task Create_Rejects_Missing_TargetUnit()
    {
        var (service, substitutions) = Build();
        var command = new CreateSubstitutionCommand(
            TargetProduct, 400m, Guid.Empty, SubstituteProduct, 154m, SubstituteUnit);

        var result = await service.ExecuteAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidUnit", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Fact(DisplayName = "Rejects a missing substitute unit id")]
    public async Task Create_Rejects_Missing_SubstituteUnit()
    {
        var (service, substitutions) = Build();
        var command = new CreateSubstitutionCommand(
            TargetProduct, 400m, TargetUnit, SubstituteProduct, 154m, Guid.Empty);

        var result = await service.ExecuteAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidUnit", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Fact(DisplayName = "Rejects self-substitution with a domain error, adding no edge")]
    public async Task Create_Rejects_SelfSubstitution()
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command(substituteProduct: TargetProduct));

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.SelfSubstitution", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Theory(DisplayName = "Rejects non-positive target quantity")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_Rejects_NonPositive_TargetQuantity(decimal quantity)
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command(targetQuantity: quantity));

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidQuantity", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Theory(DisplayName = "Rejects non-positive substitute quantity")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_Rejects_NonPositive_SubstituteQuantity(decimal quantity)
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(Command(substituteQuantity: quantity));

        Assert.False(result.IsSuccess);
        Assert.Equal("Recipes.InvalidQuantity", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }

    [Fact(DisplayName = "Rejects when no authenticated household")]
    public async Task Create_Rejects_NoHousehold()
    {
        var substitutions = new FakeSubstitutionRepository();
        var tenant = new FakeTenantContext(null);
        var service = new CreateSubstitution(substitutions, tenant, Clock, NullLogger<CreateSubstitution>.Instance);

        var result = await service.ExecuteAsync(Command());

        Assert.False(result.IsSuccess);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(substitutions.Items);
    }
}
