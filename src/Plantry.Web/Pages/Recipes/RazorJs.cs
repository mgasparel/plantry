namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// Shared Razor-view helper for the Details ingredient-row partials (<c>_IngredientRow</c>,
/// <c>_InclusionFoldRow</c>): renders a C# string as a single-quoted JS string literal for an Alpine
/// <c>x-text</c> expression (the host attribute itself is double-quoted). Every row that mixes a
/// server-rendered value (a vulgar-fraction amount, a formatted servings/batch string) with an
/// Alpine client-side scaled fallback needs this exact escaping — one rule, one home, so it cannot
/// drift between the ingredient-amount row and the inclusion roll-up row (plantry-jun6 / plantry-4037).
/// </summary>
public static class RazorJs
{
    /// <summary>
    /// Wraps <paramref name="s"/> in single quotes, escaping backslashes and embedded single quotes so
    /// the result is always a valid JS string literal regardless of the source text.
    /// </summary>
    public static string Literal(string s) =>
        "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
