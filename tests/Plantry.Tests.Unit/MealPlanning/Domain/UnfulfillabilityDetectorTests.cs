using Plantry.MealPlanning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="UnfulfillabilityDetector.DetectAsync"/>.
/// Covers: attendee with unfulfillable Required tag, fulfillable attendee, multiple attendees,
/// no attendees with Required tags, and proof that detection uses a targeted corpus query —
/// NOT the 50-cap candidate list from SearchAsync.
/// </summary>
public sealed class UnfulfillabilityDetectorTests
{
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

    // ── (a) Attendee has Required tag T1; anyRecipeWithTag(T1) returns false ────────

    [Fact(DisplayName = "DetectAsync — attendee has Required tag, corpus has no recipe with that tag → UnfulfillableResult returned")]
    public async Task DetectAsync_RequiredTagNotInCorpus_ReturnsResult()
    {
        var tagId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var constraints = MakeConstraints((userId, [tagId], []));

        // Corpus query: no recipe carries this tag.
        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result.AttendeeId);
        Assert.Equal(tagId, result.UnfulfillableTagId);
    }

    // ── (b) Attendee has Required tag T1; anyRecipeWithTag(T1) returns true ─────────

    [Fact(DisplayName = "DetectAsync — attendee has Required tag, corpus has at least one matching recipe → null (fulfillable)")]
    public async Task DetectAsync_RequiredTagInCorpus_ReturnsNull()
    {
        var tagId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var constraints = MakeConstraints((userId, [tagId], []));

        // Corpus query: at least one recipe carries this tag.
        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Null(result);
    }

    // ── (c) Multiple attendees: A fulfillable, B unfulfillable → B's result returned ─

    [Fact(DisplayName = "DetectAsync — multiple attendees: first fulfillable, second unfulfillable → second's result returned")]
    public async Task DetectAsync_MultipleAttendees_SecondUnfulfillable_ReturnsSecond()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var aliceTag = Guid.NewGuid();
        var bobTag = Guid.NewGuid();

        // Alice has aliceTag (fulfillable: corpus has it), Bob has bobTag (unfulfillable: corpus lacks it).
        var constraints = MakeConstraints(
            (aliceId, [aliceTag], []),
            (bobId, [bobTag], []));

        // aliceTag → true (has recipes), bobTag → false (no recipes).
        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            tagId => Task.FromResult(tagId == aliceTag),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(bobId, result.AttendeeId);
        Assert.Equal(bobTag, result.UnfulfillableTagId);
    }

    // ── (d) No attendees with Required tags → null ────────────────────────────────────

    [Fact(DisplayName = "DetectAsync — no attendees have Required tags → null (always fulfillable)")]
    public async Task DetectAsync_NoRequiredTags_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var constraints = MakeConstraints((userId, [], []));

        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            _ => Task.FromResult(false), // corpus always returns false — but no Required tags to check
            CancellationToken.None);

        Assert.Null(result);
    }

    // ── (e) Empty constraints (no attendees at all) → null ───────────────────────────

    [Fact(DisplayName = "DetectAsync — no attendees at all → null")]
    public async Task DetectAsync_NoAttendees_ReturnsNull()
    {
        var constraints = GenerationConstraints.Empty;

        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.Null(result);
    }

    // ── (f) Prove targeted query — not the 50-cap candidate list ────────────────────
    //
    // This test proves that UnfulfillabilityDetector calls anyRecipeWithTag directly,
    // NOT the 50-cap candidate list from SearchAsync. The detector returns a result
    // (unfulfillable) when anyRecipeWithTag returns false — regardless of what the
    // candidate list would have contained. The mock here simulates: imagine the book has
    // 60 recipes and the vegetarian one is recipe #51 (outside the 50-cap list) — but the
    // targeted anyRecipeWithTag call WOULD return true for it. Conversely, if anyRecipeWithTag
    // returns false, we're confident the full corpus has nothing.
    //
    // Concretely: if the detector reused the candidate list, it would need a CandidateRecipe
    // parameter — it does not. It only calls anyRecipeWithTag. This test verifies that
    // a tag that "would have been outside the top 50" is treated correctly: when
    // anyRecipeWithTag(tag) returns true, the cell is fulfillable (null result); when
    // anyRecipeWithTag(tag) returns false, it's unfulfillable.

    [Fact(DisplayName = "DetectAsync — uses targeted corpus query, not 50-cap candidate list: tag outside top-50 is still fulfillable when anyRecipeWithTag returns true")]
    public async Task DetectAsync_UsesTargetedCorpusQuery_NotCandidateList()
    {
        // This tag represents a "vegetarian" tag where recipes exist in the full corpus
        // but would NOT appear in a 50-cap SearchAsync call (imagine recipe #51..60 are vegetarian).
        // anyRecipeWithTag correctly returns true for the full corpus.
        var vegetarianTag = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var constraints = MakeConstraints((userId, [vegetarianTag], []));

        // The targeted query returns true — at least one recipe exists in the FULL corpus.
        // If the detector had used the 50-cap list, it would have found ZERO candidates with this tag
        // and incorrectly returned an UnfulfillableResult.
        // The detector calls anyRecipeWithTag(vegetarianTag) → true → returns null (fulfillable).
        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            tagId =>
            {
                // Only vegetarianTag returns true — simulating "recipe #51 is vegetarian, outside top-50".
                // The 50-cap candidate list would have returned empty; the targeted query returns true.
                return Task.FromResult(tagId == vegetarianTag);
            },
            CancellationToken.None);

        // Null = fulfillable: the targeted query found a recipe in the full corpus.
        // This would NOT be null if the detector had used the 50-cap candidate list.
        Assert.Null(result);
    }

    [Fact(DisplayName = "DetectAsync — uses targeted corpus query: when anyRecipeWithTag returns false, cell is unfulfillable even if it would have been in a non-empty candidate list context")]
    public async Task DetectAsync_TargetedQuery_ReturnsFalse_IsUnfulfillable()
    {
        // This tag represents a dietary need with genuinely ZERO recipes in the entire corpus.
        // anyRecipeWithTag returns false — confirming the corpus has nothing.
        var rareTag = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var constraints = MakeConstraints((userId, [rareTag], []));

        // The targeted query says: no recipe in the full corpus carries rareTag.
        // Note: we pass NO candidate list to the detector — it doesn't need one.
        // This is the key design invariant: the 50-cap list is for the AI planner, not for unfulfillability.
        var result = await UnfulfillabilityDetector.DetectAsync(
            constraints,
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result.AttendeeId);
        Assert.Equal(rareTag, result.UnfulfillableTagId);
    }
}
