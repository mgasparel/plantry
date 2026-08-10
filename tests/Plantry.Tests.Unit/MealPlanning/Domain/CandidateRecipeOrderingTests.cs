using Plantry.Planning.Domain;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

public sealed class CandidateRecipeOrderingTests
{
    private static readonly Guid ExpensiveId = Guid.Parse("0193b4a0-2222-7000-8000-000000000001");
    private static readonly Guid CheapId = Guid.Parse("0193b4a0-2222-7000-8000-000000000002");
    private static readonly Guid UnknownId = Guid.Parse("0193b4a0-2222-7000-8000-000000000003");
    private static readonly Guid PartialId = Guid.Parse("0193b4a0-2222-7000-8000-000000000004");

    [Fact]
    public void Cost_Weight_Prefers_Lower_Complete_Cost_Even_When_Name_Sorts_Later()
    {
        var candidates = new[]
        {
            new CandidateRecipe(ExpensiveId, "Alpha Expensive", [], 4, 10m,
                CostCompleteness: CandidateCostCompleteness.Complete),
            new CandidateRecipe(CheapId, "Zulu Cheap", [], 4, 2m,
                CostCompleteness: CandidateCostCompleteness.Complete),
        };

        var ordered = CandidateRecipeOrdering.Order(candidates, new PlanningWeights(0, 100, 0));

        Assert.Equal(CheapId, ordered[0].RecipeId);
        Assert.Equal(ExpensiveId, ordered[1].RecipeId);
    }

    [Fact]
    public void Waste_Weight_Prefers_Positive_Use_Soon_Allocation_Even_When_Name_Sorts_Later()
    {
        var candidates = new[]
        {
            new CandidateRecipe(ExpensiveId, "Alpha No Expiry", [], 4, null,
                HasContributingExpiringStock: false),
            new CandidateRecipe(CheapId, "Zulu Use Soon", [], 4, null,
                HasContributingExpiringStock: true),
        };

        var ordered = CandidateRecipeOrdering.Order(candidates, new PlanningWeights(100, 0, 0));

        Assert.Equal(CheapId, ordered[0].RecipeId);
        Assert.Equal(ExpensiveId, ordered[1].RecipeId);
    }

    [Fact]
    public void Unknown_And_Partial_Costs_Never_Win_As_Zero_Over_Complete_Evidence()
    {
        var completeId = Guid.Parse("0193b4a0-2222-7000-8000-000000000005");
        var candidates = new[]
        {
            new CandidateRecipe(UnknownId, "Alpha Unknown", [], 4, null,
                CostCompleteness: CandidateCostCompleteness.Unknown),
            new CandidateRecipe(PartialId, "Beta Partial", [], 4, 0.01m,
                CostCompleteness: CandidateCostCompleteness.Partial),
            new CandidateRecipe(completeId, "Zulu Complete", [], 4, 10m,
                CostCompleteness: CandidateCostCompleteness.Complete),
        };

        var ordered = CandidateRecipeOrdering.Order(candidates, new PlanningWeights(0, 100, 0));

        Assert.Equal(completeId, ordered[0].RecipeId);
        Assert.NotEqual(UnknownId, ordered[0].RecipeId);
        Assert.NotEqual(PartialId, ordered[0].RecipeId);
    }
}
