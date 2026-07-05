using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// Application service behind the recipe editor's edit-moment tag suggestion (plantry-qll2.2). Orchestrates
/// the whole read-side flow the page model needs, so the gate check and the anti-corruption reads live in
/// Recipes.Application (its proper home) rather than leaking into the Web page model:
/// <list type="number">
///   <item>Short-circuit on the household assistive-AI gate (<see cref="IAiAssistanceGateReader"/>) —
///   toggle off ⇒ no LLM call, empty result, editor unchanged (acceptance criterion 3).</item>
///   <item>Resolve the recipe's product ids to ingredient <b>names</b> via the Catalog ACL
///   (<see cref="ICatalogProductReader.ResolveSummariesAsync"/>) — <c>Ingredient</c> carries no name, so
///   names are resolved cross-context exactly as AuthorRecipe/CookRecipe already do.</item>
///   <item>Load the household's active tag vocabulary (names + categories) as suggester context.</item>
///   <item>Call the untrusted <see cref="IRecipeTagSuggester"/> and return its proposals.</item>
/// </list>
/// Returns proposals only; nothing is applied or minted here (Gate 5 — the user's tap in the editor
/// commits). Fires once per create/import from the page model, not on every save (the contradiction nudge,
/// sibling bead qll2.3, covers edits).
/// </summary>
public sealed class SuggestRecipeTags(
    IAiAssistanceGateReader gate,
    ICatalogProductReader products,
    ITagRepository tags,
    IRecipeTagSuggester suggester)
{
    /// <summary>
    /// Suggests tags for the recipe currently being authored, given its chosen product ids. Returns an
    /// empty list — never throws — when the gate is off, no product resolves to a name, or the suggester
    /// soft-fails. Duplicate ids are collapsed before resolution.
    /// </summary>
    public async Task<IReadOnlyList<TagSuggestion>> ExecuteAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        // Criterion 3: the toggle governs the call itself — no gate, no LLM cost, no chips.
        if (!await gate.IsEnabledAsync(ct))
            return [];

        var distinctIds = productIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinctIds.Count == 0)
            return [];

        // Ingredient names via the Catalog ACL — Ingredient has no Name field (only ProductId/Quantity/UnitId),
        // so names are a cross-context read, batched in one round-trip (no N+1).
        var summaries = await products.ResolveSummariesAsync(distinctIds, ct);
        var ingredientNames = summaries.Values
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (ingredientNames.Count == 0)
            return [];

        // Active household tag vocabulary (names + categories) — the preferred set the suggester matches
        // against before proposing a brand-new tag.
        var vocabulary = (await tags.ListAllAsync(activeOnly: true, ct))
            .Select(t => new TagVocabularyEntry(t.Id.Value, t.Name, t.Category))
            .ToList();

        return await suggester.SuggestAsync(ingredientNames, vocabulary, ct);
    }
}
