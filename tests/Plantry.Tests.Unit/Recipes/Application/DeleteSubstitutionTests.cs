using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

public sealed class DeleteSubstitutionTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    private static (DeleteSubstitution Service, FakeSubstitutionRepository Substitutions) Build()
    {
        var substitutions = new FakeSubstitutionRepository();
        var service = new DeleteSubstitution(substitutions, NullLogger<DeleteSubstitution>.Instance);
        return (service, substitutions);
    }

    private static Substitution NewEdge() => Substitution.Create(
        Household, Guid.NewGuid(), 400m, Guid.NewGuid(), Guid.NewGuid(), 154m, Guid.NewGuid(), Clock);

    [Fact(DisplayName = "Deletes an existing edge")]
    public async Task Delete_Removes_Existing_Edge()
    {
        var (service, substitutions) = Build();
        var edge = NewEdge();
        substitutions.Items.Add(edge);

        var result = await service.ExecuteAsync(new DeleteSubstitutionCommand(edge.Id));

        Assert.True(result.IsSuccess);
        Assert.Empty(substitutions.Items);
        Assert.Equal(1, substitutions.SaveChangesCalls);
    }

    [Fact(DisplayName = "Deleting an already-absent edge is a no-op success")]
    public async Task Delete_Missing_Edge_Is_NoOp_Success()
    {
        var (service, substitutions) = Build();

        var result = await service.ExecuteAsync(new DeleteSubstitutionCommand(SubstitutionId.New()));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, substitutions.SaveChangesCalls);
    }

    [Fact(DisplayName = "Deleting one edge never touches another")]
    public async Task Delete_Only_Removes_The_Targeted_Edge()
    {
        var (service, substitutions) = Build();
        var edgeA = NewEdge();
        var edgeB = NewEdge();
        substitutions.Items.Add(edgeA);
        substitutions.Items.Add(edgeB);

        var result = await service.ExecuteAsync(new DeleteSubstitutionCommand(edgeA.Id));

        Assert.True(result.IsSuccess);
        var remaining = Assert.Single(substitutions.Items);
        Assert.Equal(edgeB.Id, remaining.Id);
    }
}
