using Plantry.Planning.Domain;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

public sealed class CandidateRecipeShortlistingTests
{
    private static readonly Guid ProteinA = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ProteinB = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid CuisineA = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid CuisineB = Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid FlavorA = Guid.Parse("30000000-0000-0000-0000-000000000005");
    private static readonly Guid FlavorB = Guid.Parse("30000000-0000-0000-0000-000000000006");
    private static readonly Guid DietA = Guid.Parse("30000000-0000-0000-0000-000000000007");
    private static readonly Guid DietB = Guid.Parse("30000000-0000-0000-0000-000000000008");

    [Fact]
    public void Small_Corpus_Is_Preserved_Exactly()
    {
        var candidates = Enumerable.Range(0, 200).Select(Candidate).ToList();
        var result = CandidateRecipeShortlisting.Select(candidates, GenerationConstraints.Empty, DefaultWeights);
        Assert.Equal(candidates.Select(c => c.RecipeId), result.Select(c => c.RecipeId));
    }

    [Fact]
    public void Large_Corpus_Is_Capped_At_200()
    {
        var result = CandidateRecipeShortlisting.Select(Enumerable.Range(0, 250).Select(Candidate).ToList(), GenerationConstraints.Empty, DefaultWeights);
        Assert.Equal(200, result.Count);
        Assert.Equal(200, result.Select(c => c.RecipeId).Distinct().Count());
    }

    [Fact]
    public void Restrictive_Large_Corpus_Retains_Hard_Feasible_Witness()
    {
        var required = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 250).Select(Candidate).ToList();
        candidates[^1] = Candidate(249) with { TagIds = [required] };
        var constraints = new GenerationConstraints([], [new AttendeeHardStances(Guid.NewGuid(), [required], [])], new Dictionary<Guid, float>());
        var result = CandidateRecipeShortlisting.Select(candidates, constraints, DefaultWeights);
        Assert.Contains(result, c => c.RecipeId == candidates[^1].RecipeId);
    }

    [Fact]
    public void Hard_Eligible_Corpus_Does_Not_Crowd_Out_All_Confirmed_Facets()
    {
        var candidates = Enumerable.Range(0, 220).Select(Candidate).ToList();
        var required = Guid.NewGuid();
        candidates[0] = candidates[0] with { TagIds = [required] };
        candidates.AddRange([
            FacetCandidate(1000, ProteinB, CuisineB, FlavorB, DietB),
            FacetCandidate(1001, ProteinA, CuisineA, FlavorA, DietA)]);
        var constraints = new GenerationConstraints([], [new AttendeeHardStances(Guid.NewGuid(), [required], [])], new Dictionary<Guid, float>());
        var result = CandidateRecipeShortlisting.Select(candidates, constraints, DefaultWeights);
        Assert.Contains(result, c => c.RecipeId == candidates[0].RecipeId);
        Assert.Contains(result, c => c.DiversityProfile?.Protein.Any(v => v.TagId == ProteinB) == true);
        Assert.Contains(result, c => c.DiversityProfile?.Cuisine.Any(v => v.TagId == CuisineB) == true);
        Assert.Contains(result, c => c.DiversityProfile?.Flavor.Any(v => v.TagId == FlavorB) == true);
        Assert.Contains(result, c => c.DiversityProfile?.Diet.Any(v => v.TagId == DietB) == true);
    }

    [Fact]
    public void Stable_Identity_Tie_Is_Independent_Of_Input_Order()
    {
        var candidates = Enumerable.Range(0, 250).Select(Candidate).ToList();
        var a = CandidateRecipeShortlisting.Select(candidates, GenerationConstraints.Empty, DefaultWeights);
        var b = CandidateRecipeShortlisting.Select(candidates.AsEnumerable().Reverse().ToList(), GenerationConstraints.Empty, DefaultWeights);
        Assert.Equal(a.Select(c => c.RecipeId), b.Select(c => c.RecipeId));
    }

    [Fact]
    public void Late_High_Score_Candidate_Is_Retained()
    {
        var candidates = Enumerable.Range(0, 250).Select(Candidate).ToList();
        var late = candidates[^1] with { HasContributingExpiringStock = true };
        candidates[^1] = late;
        var result = CandidateRecipeShortlisting.Select(candidates, GenerationConstraints.Empty, new PlanningWeights(100, 0, 0));
        Assert.Contains(result, c => c.RecipeId == late.RecipeId);
    }

    private static PlanningWeights DefaultWeights => new(60, 20, 20);
    private static CandidateRecipe Candidate(int i) => new(Guid.Parse($"40000000-0000-0000-0000-{i + 1:000000000000}"), $"Recipe {i}", [], 4, null);
    private static CandidateRecipe FacetCandidate(int i, Guid protein, Guid cuisine, Guid flavor, Guid diet) =>
        Candidate(i) with { DiversityProfile = new RecipeDiversityProfile(
            [new("exact", "exact", null, RecipeDiversityEvidenceSource.ConfirmedRecipeFact)],
            [new(diet.ToString(), "diet", diet, RecipeDiversityEvidenceSource.ConfirmedTag)],
            [new(protein.ToString(), "protein", protein, RecipeDiversityEvidenceSource.ConfirmedTag)],
            [new(cuisine.ToString(), "cuisine", cuisine, RecipeDiversityEvidenceSource.ConfirmedTag)],
            [new(flavor.ToString(), "flavor", flavor, RecipeDiversityEvidenceSource.ConfirmedTag)]) };
}
