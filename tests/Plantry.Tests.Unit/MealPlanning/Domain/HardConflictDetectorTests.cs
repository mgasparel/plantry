using Plantry.MealPlanning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="HardConflictDetector.Detect"/>.
/// Covers: vegan vs meat → conflict; reconcilable by one recipe → null;
/// single attendee → null; Required-vs-Restricted clash → conflict.
/// </summary>
public sealed class HardConflictDetectorTests
{
    private static readonly MealSlotId SlotId = MealSlotId.New();

    private static GenerationConstraints MakeConstraints(
        params (Guid UserId, Guid[] Required, Guid[] Restricted)[] stances)
    {
        var attendeeStances = stances
            .Select(s => new AttendeeHardStances(
                s.UserId,
                (IReadOnlyCollection<Guid>)s.Required.ToHashSet(),
                (IReadOnlyCollection<Guid>)s.Restricted.ToHashSet()))
            .ToList();

        return new GenerationConstraints(
            EffectiveAttendees: stances.Select(s => s.UserId).ToList(),
            AttendeeStances: attendeeStances,
            PreferredTagWeights: new Dictionary<Guid, float>());
    }

    private static CandidateRecipe MakeCandidate(Guid recipeId, params Guid[] tagIds) =>
        new(recipeId, "Test Recipe", tagIds.ToList(), DefaultServings: 4, CostPerServing: null);

    // ── (a) vegan-Required vs meat-Required, no candidate carries both ────────────

    [Fact(DisplayName = "Detect — vegan-Required vs meat-Required, no shared recipe → conflict returned")]
    public void Detect_VeganVsMeat_NoSharedCandidate_ReturnsConflict()
    {
        var veganTag = Guid.NewGuid();
        var meatTag = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        var constraints = MakeConstraints(
            (aliceId, [veganTag], []),  // Alice: vegan-Required
            (bobId, [meatTag], []));    // Bob: meat-Required

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(Guid.NewGuid(), veganTag),      // satisfies Alice only
            MakeCandidate(Guid.NewGuid(), meatTag),       // satisfies Bob only
            MakeCandidate(Guid.NewGuid(), Guid.NewGuid()) // satisfies neither
        };

        var conflict = HardConflictDetector.Detect(constraints, candidates);

        Assert.NotNull(conflict);
        Assert.Contains(aliceId, conflict.AttendeeIds);
        Assert.Contains(bobId, conflict.AttendeeIds);
        Assert.Contains(veganTag, conflict.ClashingTagIds);
        Assert.Contains(meatTag, conflict.ClashingTagIds);
    }

    // ── (b) same two attendees, but a candidate tagged BOTH vegan+meat → null ────

    [Fact(DisplayName = "Detect — one candidate tagged both vegan and meat → reconcilable, returns null")]
    public void Detect_OneRecipeCarriesBothTags_ReturnsNull()
    {
        var veganTag = Guid.NewGuid();
        var meatTag = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        var constraints = MakeConstraints(
            (aliceId, [veganTag], []),
            (bobId, [meatTag], []));

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(Guid.NewGuid(), veganTag, meatTag) // satisfies BOTH attendees
        };

        var conflict = HardConflictDetector.Detect(constraints, candidates);

        Assert.Null(conflict);
    }

    // ── (c) single attendee → never a conflict ───────────────────────────────────

    [Fact(DisplayName = "Detect — single attendee with Required tag → not a conflict (null)")]
    public void Detect_SingleAttendee_ReturnsNull()
    {
        var requiredTag = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var constraints = MakeConstraints(
            (userId, [requiredTag], []));

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(Guid.NewGuid(), Guid.NewGuid()) // doesn't satisfy, but only 1 attendee
        };

        // Conflict detection requires ≥2 attendees with hard stances.
        var conflict = HardConflictDetector.Detect(constraints, candidates);

        Assert.Null(conflict);
    }

    // ── (d) Required-vs-Restricted clash: A requires tag T, B restricts tag T ────

    [Fact(DisplayName = "Detect — A requires tag T, B restricts tag T, no candidate avoids+includes → conflict")]
    public void Detect_RequiredVsRestrictedClash_ReturnsConflict()
    {
        var sharedTag = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        var constraints = MakeConstraints(
            (aliceId, [sharedTag], []),    // Alice: sharedTag Required
            (bobId, [], [sharedTag]));     // Bob: sharedTag Restricted

        // Only candidate: carries sharedTag → satisfies Alice but violates Bob's Restriction.
        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(Guid.NewGuid(), sharedTag)
        };

        var conflict = HardConflictDetector.Detect(constraints, candidates);

        Assert.NotNull(conflict);
        Assert.Contains(aliceId, conflict.AttendeeIds);
        Assert.Contains(bobId, conflict.AttendeeIds);
    }

    // ── (d-extra) no candidates at all → conflict when ≥2 attendees have stances ──

    [Fact(DisplayName = "Detect — no candidates, two attendees with stances → conflict")]
    public void Detect_NoCandidates_TwoAttendeesWithStances_ReturnsConflict()
    {
        var tagA = Guid.NewGuid();
        var tagB = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        var constraints = MakeConstraints(
            (aliceId, [tagA], []),
            (bobId, [tagB], []));

        var candidates = new List<CandidateRecipe>(); // empty pool

        var conflict = HardConflictDetector.Detect(constraints, candidates);

        Assert.NotNull(conflict);
    }

    // ── two attendees but only one has hard stances → not a conflict ─────────────

    [Fact(DisplayName = "Detect — two attendees but only one has hard stances → null (not irreconcilable)")]
    public void Detect_TwoAttendees_OnlyOneHasStances_ReturnsNull()
    {
        var requiredTag = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        // Alice has a Required stance, Bob has no stances.
        var constraints = MakeConstraints(
            (aliceId, [requiredTag], []),
            (bobId, [], []));

        var candidates = new List<CandidateRecipe>
        {
            MakeCandidate(Guid.NewGuid(), requiredTag) // satisfies Alice; Bob has no constraint
        };

        var conflict = HardConflictDetector.Detect(constraints, candidates);

        // Only 1 attendee has a hard stance → not "≥2 with stances" → not irreconcilable.
        Assert.Null(conflict);
    }
}
