namespace Plantry.Recipes.Application;

/// <summary>
/// One AI-detected clash between a recipe ingredient and one of the recipe's Diet-category tags
/// (plantry-qll2.3) — e.g. ingredient "Parmesan" against the "Dairy-Free" tag. Advisory only: the nudge
/// surfaces it, but nothing is ever auto-corrected — the user removes or keeps the tag themselves
/// (C9 / Gate 5). The AI proposes the observation; it never mutates the tag list.
/// </summary>
/// <param name="IngredientName">The offending ingredient's display name, as shown in the nudge copy.</param>
/// <param name="DietTagName">The Diet-category tag the ingredient appears to contradict (verbatim from the passed-in set).</param>
public sealed record DietTagContradiction(string IngredientName, string DietTagName);

/// <summary>
/// The edit-moment diet-tag contradiction checker (plantry-qll2.3): given a recipe's ingredient NAMES and its
/// Diet-category tag names, it asks the LLM whether any ingredient plainly contradicts a diet stance ("added
/// parmesan to a dairy-free recipe"). The recipes twin of Deals' <c>IDealMatcher</c> and qll2.2's
/// <c>IRecipeTagSuggester</c> — an <b>untrusted external function</b> (ADR-007) that can only observe, never
/// act.
///
/// <para><b>Inputs are passed in, not fetched here.</b> Exactly like the tag suggester, the caller
/// (<c>DietTagNudgeService</c>) resolves the ingredient names via the Catalog ACL and loads the Diet-category
/// tags; this keeps the adapter store-free and unit-testable with faked inputs. The gate check and the
/// ProductId-set guard are the caller's job, not the checker's.</para>
///
/// <para><b>Soft-fail (ADR-007).</b> Any API error, an empty response, or a malformed payload degrades to an
/// <b>empty list</b> (no nudge is shown) and <b>never throws into the caller</b>, mirroring
/// <c>DealMatcher</c> / <c>RecipeTagSuggester</c>. An empty result is also the correct answer when nothing
/// contradicts — the common case.</para>
/// </summary>
public interface IDietTagContradictionChecker
{
    /// <summary>
    /// Returns the ingredient/diet-tag contradictions the model can confidently identify from
    /// <paramref name="ingredientNames"/> against <paramref name="dietTagNames"/>. Each returned
    /// <see cref="DietTagContradiction.DietTagName"/> is one of the supplied <paramref name="dietTagNames"/>
    /// (verbatim); anything else is dropped. Returns an empty list — never throws — on any failure, on an
    /// empty input, or when nothing contradicts.
    /// </summary>
    Task<IReadOnlyList<DietTagContradiction>> CheckAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> dietTagNames,
        CancellationToken ct = default);
}
