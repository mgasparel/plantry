using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="MealConstraintResolver.ResolveForGeneration"/>.
/// Covers: empty attendees → empty constraints; hard-stance union (Required + Restricted);
/// per-attendee AttendeeStances carry separate entries per user; soft-bias average (Preferred/Disliked).
/// </summary>
public sealed class MealConstraintResolverGenerationTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly MealConstraintResolver _resolver = new();

    private static MealSlotConfig MakeConfig() =>
        MealSlotConfig.CreateWithDefaults(Household, SystemClock.Instance);

    private static UserPreference MakePref(Guid userId, params (Guid TagId, string Stance)[] stances)
    {
        var pref = UserPreference.Create(Household, userId, Clock);
        foreach (var (tagId, stance) in stances)
            pref.SetStance(tagId, stance, Clock);
        return pref;
    }

    // ── no attendees ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ResolveForGeneration_NoAttendees_ReturnsEmpty")]
    public void ResolveForGeneration_NoAttendees_ReturnsEmpty()
    {
        var config = MakeConfig();
        var breakfastSlot = config.Slots.First();
        // Ensure no attendees
        config.SetDefaultAttendees(breakfastSlot.Id, [], Clock);

        var result = _resolver.ResolveForGeneration(breakfastSlot.Id, breakfastSlot, []);

        Assert.Empty(result.EffectiveAttendees);
        Assert.Empty(result.RequiredTagIds);
        Assert.Empty(result.RestrictedTagIds);
        Assert.Empty(result.PreferredTagWeights);
    }

    // ── hard-stance union (Restricted) ───────────────────────────────────────────

    [Fact(DisplayName = "ResolveForGeneration_HardUnion_RestrictedCombined")]
    public void ResolveForGeneration_HardUnion_RestrictedCombined()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var treeNutsTag = Guid.NewGuid();
        var dairyTag = Guid.NewGuid();

        var config = MakeConfig();
        var slot = config.Slots.First();
        config.SetDefaultAttendees(slot.Id, [aliceId, bobId], Clock);

        var alice = MakePref(aliceId, (treeNutsTag, "Restricted"));
        var bob = MakePref(bobId, (dairyTag, "Restricted"));

        var result = _resolver.ResolveForGeneration(slot.Id, slot, [alice, bob]);

        // Derived union still contains both restricted tags.
        Assert.Contains(treeNutsTag, result.RestrictedTagIds);
        Assert.Contains(dairyTag, result.RestrictedTagIds);
        Assert.Empty(result.RequiredTagIds);

        // Per-attendee: each attendee's own Restricted tag is on their own entry.
        var aliceStance = result.AttendeeStances.Single(a => a.UserId == aliceId);
        var bobStance = result.AttendeeStances.Single(a => a.UserId == bobId);
        Assert.Contains(treeNutsTag, aliceStance.RestrictedTagIds);
        Assert.DoesNotContain(dairyTag, aliceStance.RestrictedTagIds);
        Assert.Contains(dairyTag, bobStance.RestrictedTagIds);
        Assert.DoesNotContain(treeNutsTag, bobStance.RestrictedTagIds);
    }

    // ── hard-stance union (Required) ─────────────────────────────────────────────

    [Fact(DisplayName = "ResolveForGeneration_HardUnion_RequiredCombined")]
    public void ResolveForGeneration_HardUnion_RequiredCombined()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var veganTag = Guid.NewGuid();
        var halalTag = Guid.NewGuid();

        var config = MakeConfig();
        var slot = config.Slots.First();
        config.SetDefaultAttendees(slot.Id, [aliceId, bobId], Clock);

        var alice = MakePref(aliceId, (veganTag, "Required"));
        var bob = MakePref(bobId, (halalTag, "Required"));

        var result = _resolver.ResolveForGeneration(slot.Id, slot, [alice, bob]);

        // Derived union still contains both required tags.
        Assert.Contains(veganTag, result.RequiredTagIds);
        Assert.Contains(halalTag, result.RequiredTagIds);
        Assert.Empty(result.RestrictedTagIds);

        // Per-attendee: Alice's Required tag is on her entry, Bob's on his — kept SEPARATE.
        var aliceStance = result.AttendeeStances.Single(a => a.UserId == aliceId);
        var bobStance = result.AttendeeStances.Single(a => a.UserId == bobId);
        Assert.Contains(veganTag, aliceStance.RequiredTagIds);
        Assert.DoesNotContain(halalTag, aliceStance.RequiredTagIds);
        Assert.Contains(halalTag, bobStance.RequiredTagIds);
        Assert.DoesNotContain(veganTag, bobStance.RequiredTagIds);
    }

    // ── soft average ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ResolveForGeneration_SoftAverage_PreferredBias_CancelsOut")]
    public void ResolveForGeneration_SoftAverage_PreferredBias_CancelsOut()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var spicyTag = Guid.NewGuid();

        var config = MakeConfig();
        var slot = config.Slots.First();
        config.SetDefaultAttendees(slot.Id, [aliceId, bobId], Clock);

        var alice = MakePref(aliceId, (spicyTag, "Preferred"));   // +1
        var bob = MakePref(bobId, (spicyTag, "Disliked"));         // -1

        var result = _resolver.ResolveForGeneration(slot.Id, slot, [alice, bob]);

        // Average = (1 + -1) / 2 = 0 → tag should be absent from the map
        Assert.False(result.PreferredTagWeights.ContainsKey(spicyTag));
    }

    [Fact(DisplayName = "ResolveForGeneration_SoftAverage_NetPositive")]
    public void ResolveForGeneration_SoftAverage_NetPositive()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var spicyTag = Guid.NewGuid();

        var config = MakeConfig();
        var slot = config.Slots.First();
        config.SetDefaultAttendees(slot.Id, [aliceId, bobId], Clock);

        var alice = MakePref(aliceId, (spicyTag, "Preferred"));  // +1
        var bob = MakePref(bobId);                                // 0 (no stance)

        var result = _resolver.ResolveForGeneration(slot.Id, slot, [alice, bob]);

        // Average = (1 + 0) / 2 = 0.5 → positive bias
        Assert.True(result.PreferredTagWeights.TryGetValue(spicyTag, out var weight));
        Assert.True(weight > 0f);
    }
}
