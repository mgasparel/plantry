using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="ProposalAcl"/>.
/// Covers: nonexistent recipe ID rejection; Restricted-tag auto-drop;
/// all dishes restricted → unfilled; per-attendee Required-tag coverage (M5); valid proposal passes through.
/// </summary>
public sealed class ProposalAclTests
{
    private static readonly MealSlotId SlotId = MealSlotId.New();
    private static readonly DateOnly Date = new(2026, 6, 16);

    private static CandidateRecipe MakeCandidate(Guid recipeId, params Guid[] tagIds) =>
        new(recipeId, "Test Recipe", tagIds.ToList(), DefaultServings: 4, CostPerServing: null);

    /// <summary>
    /// Builds GenerationConstraints with per-attendee stances.
    /// Each entry in <paramref name="attendeeStances"/> is (userId, requiredTags, restrictedTags).
    /// When omitted, a single attendee with no hard stances is used.
    /// </summary>
    private static GenerationConstraints MakeConstraints(
        IReadOnlyCollection<Guid>? restricted = null,
        IReadOnlyCollection<Guid>? required = null,
        IEnumerable<(Guid UserId, IReadOnlyCollection<Guid> Required, IReadOnlyCollection<Guid> Restricted)>? attendeeStances = null)
    {
        List<AttendeeHardStances> stances;
        if (attendeeStances is not null)
        {
            stances = attendeeStances
                .Select(a => new AttendeeHardStances(a.UserId, a.Required, a.Restricted))
                .ToList();
        }
        else
        {
            // Build a single-attendee stance from the flat restricted/required shorthand.
            var userId = Guid.NewGuid();
            stances =
            [
                new AttendeeHardStances(
                    userId,
                    required ?? new HashSet<Guid>(),
                    restricted ?? new HashSet<Guid>())
            ];
        }

        return new GenerationConstraints(
            EffectiveAttendees: stances.Select(a => a.UserId).ToList(),
            AttendeeStances: stances,
            PreferredTagWeights: new Dictionary<Guid, float>());
    }

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

    // ── restricted tag auto-drop ─────────────────────────────────────────────────

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

    // ── required tag not met → unfilled (M5 / MP-O4) ──────────────────────────────

    [Fact(DisplayName = "Validate_RequiredTagNotMet_ReturnsUnfilled")]
    public void Validate_RequiredTagNotMet_ReturnsUnfilled()
    {
        var requiredTag = Guid.NewGuid();
        var otherTag = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        // Recipe exists and is not restricted, but does not carry the Required tag.
        var candidates = new List<CandidateRecipe> { MakeCandidate(recipeId, otherTag) };
        var constraints = MakeConstraints(required: new HashSet<Guid> { requiredTag });
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.False(result.IsValid);
        Assert.Null(result.ValidatedProposal);
    }

    // ── per-attendee Required coverage ───────────────────────────────────────────
    // Attendee A needs vegan, attendee B needs halal.
    // A dish tagged only {vegan} covers A but not B → Unfilled.
    // A dish tagged {vegan, halal} covers both → passes.

    [Fact(DisplayName = "Validate_PerAttendee_SingleDishCoversOnlyOne_ReturnsUnfilled")]
    public void Validate_PerAttendee_SingleDishCoversOnlyOne_ReturnsUnfilled()
    {
        var veganTag = Guid.NewGuid();
        var halalTag = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(recipeId, veganTag) // covers A, not B
        };
        var constraints = MakeConstraints(attendeeStances:
        [
            (userA, new HashSet<Guid> { veganTag }, new HashSet<Guid>()),
            (userB, new HashSet<Guid> { halalTag }, new HashSet<Guid>()),
        ]);
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.False(result.IsValid);
        Assert.Null(result.ValidatedProposal);
    }

    [Fact(DisplayName = "Validate_PerAttendee_SingleDishCoversBoth_Passes")]
    public void Validate_PerAttendee_SingleDishCoversBoth_Passes()
    {
        var veganTag = Guid.NewGuid();
        var halalTag = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(recipeId, veganTag, halalTag) // covers both A and B
        };
        var constraints = MakeConstraints(attendeeStances:
        [
            (userA, new HashSet<Guid> { veganTag }, new HashSet<Guid>()),
            (userB, new HashSet<Guid> { halalTag }, new HashSet<Guid>()),
        ]);
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.True(result.IsValid);
        Assert.NotNull(result.ValidatedProposal);
        Assert.Single(result.ValidatedProposal!.Dishes);
    }

    // ── required tag collectively covered across dishes → passes (M5 / MP-O4) ──────
    // Two Required tags both belonging to the SAME attendee — one dish covering both passes.

    [Fact(DisplayName = "Validate_RequiredTagCoveredByOneDish_PassesThrough")]
    public void Validate_RequiredTagCoveredByOneDish_PassesThrough()
    {
        var veganTag = Guid.NewGuid();
        var halalTag = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        // Single attendee with two Required tags; one dish carries both.
        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(recipeId, veganTag, halalTag),
        };
        var constraints = MakeConstraints(attendeeStances:
        [
            (userId, new HashSet<Guid> { veganTag, halalTag }, new HashSet<Guid>()),
        ]);
        var proposal = MakeProposal((recipeId, 4));

        var result = ProposalAcl.Validate(proposal, candidates, constraints);

        Assert.True(result.IsValid);
        Assert.NotNull(result.ValidatedProposal);
        Assert.Single(result.ValidatedProposal!.Dishes);
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
        Assert.NotNull(result.ValidatedProposal);
        var dish = Assert.Single(result.ValidatedProposal!.Dishes);
        Assert.Equal(recipeId, dish.RecipeId);
    }
}
