namespace Plantry.Ai.Infrastructure;

/// <summary>
/// The shared taxonomy of Plantry's AI functions — one constant per adapter, holding the exact
/// <see cref="System.Diagnostics.Activity"/> span name that adapter has always used. No renames: the
/// current span names ARE the taxonomy (plantry-df6p). Adapters use these constants for their span
/// names (<c>AiTelemetry.ActivitySource.StartActivity(AiFunction.X)</c>) and pass them to
/// <see cref="AiUsageTelemetry.RecordTokenUsage"/> as the <c>ai.function</c> metric dimension, so the
/// string can't drift between the span and the metric.
/// </summary>
public static class AiFunction
{
    /// <summary>Receipt parsing (Intake) — <see cref="System.Diagnostics.Activity"/> span <c>receipt_parse</c>.</summary>
    public const string ReceiptParse = "receipt_parse";

    /// <summary>Flyer deal matching (Market/Deals) — span <c>deal_match</c>.</summary>
    public const string DealMatch = "deal_match";

    /// <summary>Meal plan proposal (Planning) — span <c>meal_plan_propose</c>.</summary>
    public const string MealPlanPropose = "meal_plan_propose";

    /// <summary>Ingredient unit-conversion seeding (Recipes) — span <c>recipe_conversion_seed</c>.</summary>
    public const string RecipeConversionSeed = "recipe_conversion_seed";

    /// <summary>Recipe diet/style tag suggestion (Recipes) — span <c>recipe_tag_suggest</c>.</summary>
    public const string RecipeTagSuggest = "recipe_tag_suggest";

    /// <summary>Edit-moment diet-tag contradiction nudge (Recipes) — span <c>recipe_diet_nudge</c>.</summary>
    public const string RecipeDietNudge = "recipe_diet_nudge";
}
