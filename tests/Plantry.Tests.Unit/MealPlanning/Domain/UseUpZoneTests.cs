using Plantry.Planning.Domain;
using Xunit;

namespace Plantry.Tests.Unit.MealPlanning.Domain;

/// <summary>
/// L1 unit tests for <see cref="UseUpZone"/> (plantry-yuy3) — the C# port of cook-logic.js's
/// <c>isInUseUpZone</c>, used by the meal-plan Eat action's server-side auto-trigger decision.
/// Mirrors cook-logic.test.js's "isInUseUpZone" cases one-for-one (see that file's <c>describe</c>
/// block) so the two implementations can never silently drift on behaviour, only on the JS-only
/// floating-point epsilon fuzz this port doesn't need (C# <see cref="decimal"/> is exact).
/// </summary>
public sealed class UseUpZoneTests
{
    [Fact(DisplayName = "is false when nothing would be left (qty already at on-hand)")]
    public void IsInUseUpZone_False_When_Qty_Equals_OnHand()
    {
        Assert.False(UseUpZone.IsInUseUpZone(3.3m, 3.3m));
    }

    [Fact(DisplayName = "is true when the remainder is within the default 10% threshold")]
    public void IsInUseUpZone_True_Within_Default_Threshold()
    {
        // Ground beef: 3.3 on hand, recipe default 3 lb -> 0.3 left, 0.3 <= 0.33 (10% of 3.3).
        Assert.True(UseUpZone.IsInUseUpZone(3.3m, 3m));
    }

    [Fact(DisplayName = "is false when the remainder exceeds the 10% threshold")]
    public void IsInUseUpZone_False_When_Remainder_Exceeds_Threshold()
    {
        // Pasta: 600 on hand, need 400 -> 200 left, well above 60 (10% of 600).
        Assert.False(UseUpZone.IsInUseUpZone(600m, 400m));
    }

    [Fact(DisplayName = "is true right at the threshold boundary")]
    public void IsInUseUpZone_True_At_Threshold_Boundary()
    {
        // Onion: 10 on hand, stepped to 9 -> 1 left == exactly 10% of 10.
        Assert.True(UseUpZone.IsInUseUpZone(10m, 9m));
    }

    [Fact(DisplayName = "is false when on-hand is zero or negative (nothing to use up)")]
    public void IsInUseUpZone_False_When_OnHand_NonPositive()
    {
        Assert.False(UseUpZone.IsInUseUpZone(0m, 0m));
        Assert.False(UseUpZone.IsInUseUpZone(-1m, -2m));
    }

    [Fact(DisplayName = "honours a custom threshold")]
    public void IsInUseUpZone_Honours_Custom_Threshold()
    {
        Assert.True(UseUpZone.IsInUseUpZone(10m, 8m, 0.25m));  // 2 left, 25% of 10
        Assert.False(UseUpZone.IsInUseUpZone(10m, 7m, 0.25m)); // 3 left, above 25% of 10
    }

    [Fact(DisplayName = "is false when qty exceeds on-hand (nothing left to use up, already over)")]
    public void IsInUseUpZone_False_When_Qty_Exceeds_OnHand()
    {
        Assert.False(UseUpZone.IsInUseUpZone(3m, 4m));
    }
}
