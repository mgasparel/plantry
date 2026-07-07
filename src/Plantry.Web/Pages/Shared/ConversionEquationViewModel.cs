namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View-model for the shared four-field unit-conversion equation partial
/// (<c>Shared/_ConversionEquation</c>).
///
/// <para>The partial renders the two-sided equation editor — two amount inputs, two
/// axis-locked unit selects, and an "=" operator — plus the derived stock-terms echo line.
/// It is the reuse target for the markup that was duplicated between the in-sheet ingredient
/// prompt (<c>Shared/_IngredientConversionPrompt</c>, <c>draft.*</c> binding) and the
/// landed-row block in <c>Recipes/Edit</c> (<c>row.*</c> binding). Extracted per plantry-pqlo
/// (Gate 6 reuse, deferred from the plantry-wq9s review).</para>
///
/// <para>The two call sites diverge only in (a) which Alpine object holds the equation state
/// and (b) which host helper computes the echo. Everything else — the "Plantry stocks X in Y"
/// ask heading, the warning/neutral tone wrapper, the sheet-only "Measure in {default} instead"
/// escape, and the row-only "Saved to your catalog when you save the recipe" note — is
/// intentionally left at the call sites (see WHY DEFER on plantry-pqlo) and NOT part of this
/// partial.</para>
///
/// <para><b>Alpine contract:</b> the host page must expose, on the object named by
/// <see cref="Root"/>, the fields <c>convLeftAmount</c>, <c>convLeftUnitId</c>,
/// <c>convRightAmount</c>, <c>convRightUnitId</c>, <c>stockUnits</c> (LEFT axis, stock
/// dimension) and <c>recipeUnits</c> (RIGHT axis, recipe-line dimension), each unit entry
/// shaped <c>{ id, code }</c>. The host also provides the echo helper named by
/// <see cref="EchoExpression"/> / <see cref="EchoShowExpression"/>.</para>
/// </summary>
public sealed class ConversionEquationViewModel
{
    /// <summary>
    /// Name of the Alpine object holding the equation state, templated into every
    /// <c>x-model</c> / <c>x-for</c> expression. The sheet passes <c>"draft"</c>; the
    /// landed row passes <c>"row"</c> (the <c>x-for</c> loop variable in the rows template).
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Alpine expression that returns the derived stock-terms echo string (empty while the
    /// amounts are incomplete). Bound as the echo line's <c>x-text</c>. The sheet passes
    /// <c>"conversionEcho()"</c>; the landed row passes <c>"rowConversionEcho(row)"</c>.
    /// </summary>
    public required string EchoExpression { get; init; }

    /// <summary>
    /// Alpine boolean expression controlling whether the echo line is shown (bound as its
    /// <c>x-show</c>). Kept separate from <see cref="EchoExpression"/> so neither call site's
    /// show semantics change: the sheet passes <c>"conversionEcho() !== ''"</c>; the landed
    /// row passes <c>"rowConvFilled(row)"</c>.
    /// </summary>
    public required string EchoShowExpression { get; init; }
}
