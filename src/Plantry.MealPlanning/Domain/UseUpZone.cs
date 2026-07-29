namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Server-side C# port of the Cook page's "use it up" sliver-zone predicate — <c>CookLogic.isInUseUpZone</c>
/// in <c>src/Plantry.Web/wwwroot/js/islands/cook-logic.js</c> — used by the meal-plan Eat action
/// (plantry-yuy3) to decide, at render time (<c>IndexModel.LoadWeekAsync</c>), whether tapping Eat
/// should auto-open the confirm sheet instead of consuming the planned quantity directly.
///
/// This is a DELIBERATE duplication, not a shared constant: cook-logic.js is a JS-only file consumed
/// by the Cook page's classic-script Alpine island (see that file's header for why it can't be an ESM
/// module), and this codebase has no shared C#/JS module mechanism. Keep the 10% default and this
/// cross-reference in sync with cook-logic.js by hand — a change to one without the other is a drift bug.
/// </summary>
public static class UseUpZone
{
    /// <summary>Default sliver threshold: 10% of on-hand quantity — mirrors cook-logic.js's default.</summary>
    public const decimal DefaultThreshold = 0.10m;

    /// <summary>
    /// True when consuming <paramref name="quantity"/> from <paramref name="onHand"/> would leave a
    /// small sliver: strictly greater than zero, but at most <paramref name="threshold"/> (default 10%)
    /// of on-hand. Mirrors cook-logic.js's <c>isInUseUpZone</c> exactly, minus the floating-point
    /// epsilon fuzz that function needs for JS doubles — C# <see cref="decimal"/> arithmetic is exact,
    /// so no epsilon term is needed here.
    /// </summary>
    public static bool IsInUseUpZone(decimal onHand, decimal quantity, decimal threshold = DefaultThreshold)
    {
        if (onHand <= 0) return false;
        var left = onHand - quantity;
        return left > 0 && left <= onHand * threshold;
    }
}
