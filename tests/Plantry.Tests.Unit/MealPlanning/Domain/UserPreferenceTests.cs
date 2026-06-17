using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="UserPreference"/> domain behaviour (acceptance criterion L1 / M6).
/// Covers: one stance per (user, tag); Neutral leaves no row; SetStance/ClearStance semantics.
/// </summary>
public sealed class UserPreferenceTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact(DisplayName = "Create produces a profile with no stances")]
    public void Create_Produces_EmptyProfile()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);

        Assert.Equal(Household, pref.HouseholdId);
        Assert.Equal(UserId, pref.UserId);
        Assert.Empty(pref.TagStances);
    }

    [Fact(DisplayName = "SetStance(Required) adds a row")]
    public void SetStance_Required_AddsRow()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        var tagId = Guid.NewGuid();

        var added = pref.SetStance(tagId, "Required", Clock);

        Assert.True(added);
        var stance = Assert.Single(pref.TagStances);
        Assert.Equal(tagId, stance.TagId);
        Assert.Equal("Required", stance.Stance);
    }

    [Fact(DisplayName = "SetStance twice on same tag upserts (does not duplicate)")]
    public void SetStance_Twice_Upserts()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        var tagId = Guid.NewGuid();

        pref.SetStance(tagId, "Preferred", Clock);
        pref.SetStance(tagId, "Disliked", Clock);

        var stance = Assert.Single(pref.TagStances);
        Assert.Equal("Disliked", stance.Stance);
    }

    [Fact(DisplayName = "SetStance(Neutral) on a non-existent row returns false (no-op)")]
    public void SetStance_Neutral_NonExistent_ReturnsFalse()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);

        var result = pref.SetStance(Guid.NewGuid(), "Neutral", Clock);

        Assert.False(result);
        Assert.Empty(pref.TagStances);
    }

    [Fact(DisplayName = "SetStance(Neutral) on an existing row removes it (M6 — Neutral = no row)")]
    public void SetStance_Neutral_RemovesExistingRow()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        var tagId = Guid.NewGuid();
        pref.SetStance(tagId, "Required", Clock);

        var result = pref.SetStance(tagId, "Neutral", Clock);

        // ClearStanceCore returns true when a row was found and removed.
        Assert.True(result);
        Assert.Empty(pref.TagStances);
    }

    [Fact(DisplayName = "ClearStance removes the row; returns false when no row exists")]
    public void ClearStance_RemovesRow()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        var tagId = Guid.NewGuid();
        pref.SetStance(tagId, "Preferred", Clock);

        var removed = pref.ClearStance(tagId, Clock);
        Assert.True(removed);
        Assert.Empty(pref.TagStances);

        var noOp = pref.ClearStance(tagId, Clock);
        Assert.False(noOp);
    }

    [Fact(DisplayName = "Multiple stances on different tags are each stored independently")]
    public void MultipleStances_DifferentTags_StoredIndependently()
    {
        var pref = UserPreference.Create(Household, UserId, Clock);
        var tag1 = Guid.NewGuid();
        var tag2 = Guid.NewGuid();

        pref.SetStance(tag1, "Required", Clock);
        pref.SetStance(tag2, "Restricted", Clock);

        Assert.Equal(2, pref.TagStances.Count);
        Assert.Contains(pref.TagStances, ts => ts.TagId == tag1 && ts.Stance == "Required");
        Assert.Contains(pref.TagStances, ts => ts.TagId == tag2 && ts.Stance == "Restricted");
    }

    [Theory(DisplayName = "SetStance rejects invalid stance values")]
    [InlineData("Meh")]
    [InlineData("neutral")] // case-sensitive
    [InlineData("")]
    [InlineData("Unknown")]
    public void SetStance_InvalidValue_Throws(string badStance)
    {
        var pref = UserPreference.Create(Household, UserId, Clock);

        Assert.Throws<ArgumentException>(() => pref.SetStance(Guid.NewGuid(), badStance, Clock));
    }
}
