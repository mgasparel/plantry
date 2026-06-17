using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="ProposalAcl"/>.
/// Covers: nonexistent recipe ID rejection; Restricted-tag auto-drop (simplified C6);
/// all dishes restricted → unfilled; valid proposal passes through.
/// </summary>
public sealed class ProposalAclTests
{
    private static readonly MealSlotId SlotId = MealSlotId.New();
    private static readonly DateOnly Date = new(2026, 6, 16);

    private static CandidateRecipe MakeCandidate(Guid recipeId, params Guid[] tagIds) =>
        new(recipeId, "Test Recipe", tagIds.ToList(), DefaultServings: 4, CostPerServing: null);

    private static GenerationConstraints MakeConstraints(
        IReadOnlyCollection<Guid>? restricted = null,
        IReadOnlyCollection<Guid>? required = null) =>
        new(
            EffectiveAttendees: [Guid.NewGuid()],
            RequiredTagIds: required ?? new HashSet<Guid>(),
            RestrictedTagIds: restricted ?? new HashSet<Guid>(),
            PreferredTagWeights: new Dictionary<Guid, float>());

    private static ProposedMeal MakeProposal(params (Guid RecipeId, int Servings)[] dishes) =>
        new(
            Date: Date,
            MealSlotId: SlotId,
            EffectiveAttendees: [Guid.NewGuid()],
            Dishes: dishes.Select((d, i) => new ProposedDish(d.RecipeId, d.Servings, i + 1)).ToList(),
            Reasoning: "Test");

    // ── nonexistent recipe ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Validate_NonexistentRecipe_ReturnsInvalid")]
    public void Validate_NonexistentRecipe_ReturnsInvalid()
    {
        var hallucinated = Guid.NewGuid();
        var proposal = MakeProposal((hallucinated, 4));
        var candidates = new List<CandidateRecipe>(); // empty — no matching recipe
        var constraints = MakeConstraints();

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.False(result.IsValid);
        Assert.Null(result.ValidatedProposal);
    }

    // ── restricted tag auto-drop (simplified C6) ─────────────────────────────────

    [Fact(DisplayName = "Validate_RestrictedTag_DropsConflictingDish")]
    public void Validate_RestrictedTag_DropsConflictingDish()
    {
        var restrictedTag = Guid.NewGuid();
        var okTag = Guid.NewGuid();
        var restrictedRecipeId = Guid.NewGuid();
        var okRecipeId = Guid.NewGuid();

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(restrictedRecipeId, restrictedTag),
            MakeCandidate(okRecipeId, okTag),
        };
        var constraints = MakeConstraints(restricted: new HashSet<Guid> { restrictedTag });
        var proposal = MakeProposal((restrictedRecipeId, 4), (okRecipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.True(result.IsValid);
        Assert.True(result.WasSplit);
        Assert.NotNull(result.ValidatedProposal);
        Assert.DoesNotContain(result.ValidatedProposal!.Dishes, d => d.RecipeId == restrictedRecipeId);
        Assert.Contains(result.ValidatedProposal!.Dishes, d => d.RecipeId == okRecipeId);
    }

    // ── all dishes restricted → unfilled ─────────────────────────────────────────

    [Fact(DisplayName = "Validate_NoValidOption_ReturnsUnfilled")]
    public void Validate_NoValidOption_ReturnsUnfilled()
    {
        var restrictedTag = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var candidates = new List<CandidateRecipe> { MakeCandidate(recipeId, restrictedTag) };
        var constraints = MakeConstraints(restricted: new HashSet<Guid> { restrictedTag });
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.False(result.IsValid);
        Assert.Null(result.ValidatedProposal);
    }

    // ── valid proposal passes through ─────────────────────────────────────────────

    [Fact(DisplayName = "Validate_ValidProposal_PassesThrough")]
    public void Validate_ValidProposal_PassesThrough()
    {
        var tag = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var candidates = new List<CandidateRecipe> { MakeCandidate(recipeId, tag) };
        var constraints = MakeConstraints(); // no restricted tags
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.True(result.IsValid);
        Assert.False(result.WasSplit);
        Assert.NotNull(result.ValidatedProposal);
        var dish = Assert.Single(result.ValidatedProposal!.Dishes);
        Assert.Equal(recipeId, dish.RecipeId);
    }
}
