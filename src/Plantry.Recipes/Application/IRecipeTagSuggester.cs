using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// An existing household tag offered to the suggester as vocabulary context (plantry-qll2.2). The
/// recipe-tag twin of Deals' <c>ProductCandidate</c>: the caller (<see cref="SuggestRecipeTags"/>)
/// loads the active household tags and passes them <b>in</b> — the suggester never touches the tag
/// store. A returned suggestion is treated as an existing-tag pick only when its name matches one of
/// these entries (case-insensitive); anything else is a would-be new tag (ADR-007 untrusted-input
/// discipline — the model can only ever propose, never reach into the vocabulary).
/// </summary>
/// <param name="TagId">The household tag id — the value carried back when a suggestion resolves to this existing tag.</param>
/// <param name="Name">The tag's display name, shown to the model as the preferred vocabulary.</param>
/// <param name="Category">The tag's cosmetic category, if set — steers the model toward diet/protein-shaped suggestions.</param>
public sealed record TagVocabularyEntry(Guid TagId, string Name, TagCategory? Category);

/// <summary>
/// One AI-proposed tag for a recipe (plantry-qll2.2). Two flavours, distinguished by
/// <see cref="ExistingTagId"/>:
/// <list type="bullet">
///   <item><b>Existing</b> (<see cref="ExistingTagId"/> non-null) — the model's suggestion matched a
///   household tag by name; accepting it applies that tag id, exactly as picking it from the dropdown would.</item>
///   <item><b>New</b> (<see cref="ExistingTagId"/> null) — the model proposed a tag not in the household
///   vocabulary; accepting it <b>mints</b> a new tag (rendered visually distinct — the dashed
///   <c>.ai-chip--new</c>). Minting only ever happens through the user's tap (Gate 5 / ADR-007).</item>
/// </list>
/// A suggestion never auto-applies: it becomes a recipe tag only when the user taps its chip.
/// </summary>
/// <param name="Name">The suggested tag name (verbatim for an existing match; the proposed name for a new tag).</param>
/// <param name="Category">The cosmetic category — the existing tag's category for a match, or the model's proposed category (may be null) for a new tag.</param>
/// <param name="ExistingTagId">The matched household tag id, or null when this suggestion would mint a new tag.</param>
public sealed record TagSuggestion(string Name, TagCategory? Category, Guid? ExistingTagId)
{
    /// <summary>True when accepting this suggestion mints a new tag rather than applying an existing one.</summary>
    public bool IsNew => ExistingTagId is null;
}

/// <summary>
/// The edit-moment tag suggester (plantry-qll2.2): reads a recipe's ingredient names as plain text plus
/// the household's existing tag vocabulary and proposes tags the author confirms with a tap. The recipes
/// twin of Deals' <c>IDealMatcher</c> / Intake's receipt parser — an <b>untrusted external function</b>
/// (ADR-007). It attacks the tag-coverage problem: an untagged-but-suitable recipe is invisible to
/// hard-stance planner filtering, and recipe creation is the one moment coverage can be fixed for free.
/// <para>
/// <b>Vocabulary is passed in, not fetched here.</b> Exactly like <c>IDealMatcher</c> takes its candidate
/// set, the caller supplies the household tags; this keeps the adapter tag-store-free and unit-testable
/// with faked vocabulary. The gate check, name resolution, and vocabulary load are
/// <see cref="SuggestRecipeTags"/>'s job.
/// </para>
/// <para>
/// <b>A proposal only — never a write.</b> The surface returns <see cref="TagSuggestion"/>s and cannot
/// apply or mint anything: a suggestion becomes a recipe tag only through the user's tap on the editor
/// chip (Gate 5 — user confirmation commits), and a new tag is minted only on that tap.
/// </para>
/// <para>
/// <b>Soft-fail (ADR-007).</b> The AI seam is fragile: any API error, an empty response, or a malformed
/// payload degrades to an <b>empty list</b> (no chips — the editor renders nothing) and <b>never throws
/// into the caller</b>, mirroring <c>DealMatcher</c>.
/// </para>
/// </summary>
public interface IRecipeTagSuggester
{
    /// <summary>
    /// Propose tags for a recipe from its <paramref name="ingredientNames"/>, preferring names already in
    /// <paramref name="vocabulary"/> over minting new ones. <paramref name="appliedTagNames"/> carries every
    /// tag currently applied to the recipe in the editor — existing chips AND accepted-but-unsaved new-tag
    /// chips alike — so the model can avoid proposing a tag that duplicates, or is merely a subset already
    /// implied by, an applied tag (plantry-crre; e.g. skip "Vegetarian"/"Dairy-Free" when "Vegan" is
    /// applied). Returns an empty list — never throws — on any failure or when nothing plausible is found.
    /// Suggestions matching a vocabulary entry by name carry that entry's
    /// <see cref="TagVocabularyEntry.TagId"/>; the rest are new-tag proposals.
    /// </summary>
    Task<IReadOnlyList<TagSuggestion>> SuggestAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<TagVocabularyEntry> vocabulary,
        IReadOnlyList<string> appliedTagNames,
        CancellationToken ct = default);
}
