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
    /// the result is always a valid JS string literal, AND escaping every HTML-significant character —
    /// double quote, ampersand, and the opening angle bracket — as a JS unicode escape so the emitted
    /// bytes contain none of them. That second property is what makes the result safe to splice — via
    /// <c>@Html.Raw</c> — into a double-quoted HTML attribute (an Alpine <c>x-text</c>) regardless of
    /// the input: a raw double quote in the source text would otherwise terminate the attribute early
    /// and truncate everything after it (plantry-gcpb / plantry-wcmg / plantry-qrg7 / plantry-97jd — the
    /// same defect class recurring at each new splice site). Escaping the ampersand also preserves
    /// entity-decode fidelity: a literal HTML entity in the input would otherwise be decoded by the
    /// browser back into the character it names. The closing angle bracket is deliberately left
    /// unescaped (not significant in attribute values or JS string literals); non-ASCII glyphs (½, ≈)
    /// deliberately pass through unescaped for snapshot readability and byte-identity.
    /// </summary>
    /// <remarks>
    /// Today's callers only ever feed this numeric-formatter output — <c>QuantityFormatting.Format</c>'s
    /// vulgar-fraction/decimal amount strings and the constant " serving(s)"/" batch(es)" suffixes —
    /// whose output alphabet never contains a double quote, ampersand, or angle bracket today. The one
    /// user-creatable string in these rows, the unit code, is deliberately kept out of this helper and
    /// rendered Razor-encoded instead (<c>_IngredientRow.cshtml</c>). The escapes above exist so that
    /// invariant is enforced by the helper itself: a future change that routes user text through
    /// <see cref="Literal"/> cannot silently recreate the truncation defect class.
    /// </remarks>
    public static string Literal(string s) =>
        "'" + s.Replace("\\", "\\\\")
               .Replace("'", "\\'")
               .Replace("\"", "\\u0022")
               .Replace("&", "\\u0026")
               .Replace("<", "\\u003C") + "'";
}
