using Microsoft.Extensions.Logging;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// The resolved diet-tag contradiction nudge shown on the post-save recipe view (plantry-qll2.3): the offending
/// ingredient, plus the Diet-category tag it contradicts (with its id, so the "Remove &lt;tag&gt; tag" action
/// can target it). Advisory only — the save already landed and is never blocked (C9).
/// </summary>
public sealed record DietTagNudgeView(Guid RecipeId, string IngredientName, Guid DietTagId, string DietTagName);

/// <summary>
/// Application service behind the recipe editor's edit-moment diet-tag contradiction nudge (plantry-qll2.3).
/// It keeps the two-stage guard the epic (plantry-qll2) design calls for in one place:
/// <list type="number">
///   <item><see cref="ShouldOfferAfterSaveAsync"/> — the <b>cheap</b> post-save trigger the editor POST runs to
///   decide whether to defer a check at all. It fires only when the ProductId set actually changed AND the recipe
///   carries a Diet-category tag AND that new set has not already been reconciled. No LLM, no Catalog name
///   resolution — the ProductId-set hash is derived from in-aggregate ids (free). This is what keeps "most saves
///   trigger nothing" true and confines the AI strictly to the edit moment (never a corpus sweep).</item>
///   <item><see cref="EvaluateAsync"/> — the <b>deferred</b> check the post-save landing runs via htmx: the
///   household assistive-AI gate (<see cref="IAiAssistanceGateReader"/>, plantry-qll2.1), then the cross-context
///   name resolution and the untrusted LLM call, only once the guard has already fired.</item>
/// </list>
/// The nudge never mutates tags: <see cref="DismissAsync"/> ("Keep it") and <see cref="RemoveTagAsync"/>
/// ("Remove &lt;tag&gt; tag" — a <b>user</b> action) both record the current ingredient set as reconciled so it
/// does not re-nag (Gate 5 / C9).
/// </summary>
public sealed class DietTagNudgeService(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader products,
    IAiAssistanceGateReader gate,
    IDietTagContradictionChecker checker,
    IClock clock,
    ILogger<DietTagNudgeService> logger)
{
    /// <summary>
    /// Cheap, no-LLM post-save trigger guard. Returns true only when a deferred contradiction check is warranted:
    /// the recipe still exists, its distinct ProductId set differs from <paramref name="previousProductIds"/> (the
    /// set BEFORE this save — an ingredient-neutral edit changes nothing here, acceptance criterion 2), it carries
    /// at least one Diet-category tag (criterion 3), and the new set has not already been dismissed for this recipe.
    /// The household assistive-AI gate is deliberately checked later, in <see cref="EvaluateAsync"/>, so the toggle
    /// still governs the LLM call itself (criterion 4) without a gate round-trip on every save.
    /// </summary>
    public async Task<bool> ShouldOfferAfterSaveAsync(
        RecipeId recipeId, IReadOnlySet<Guid> previousProductIds, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return false;

        var currentIds = DistinctProductIds(recipe);
        if (currentIds.SetEquals(previousProductIds)) return false; // ingredient set unchanged (criterion 2)

        var dietTags = await DietTagsAsync(recipe, ct);
        if (dietTags.Count == 0) return false; // no Diet-category tag (criterion 3)

        // Already reconciled for this exact ingredient set — don't re-nag (criterion 1, "dismiss remembered").
        return recipe.CurrentIngredientProductHash() != recipe.DietNudgeDismissedHash;
    }

    /// <summary>
    /// The deferred check: re-verifies the guard, applies the assistive-AI gate, resolves ingredient names via the
    /// Catalog ACL (<c>Ingredient</c> has no Name — cross-context by construction), and asks the untrusted checker
    /// for contradictions. Returns the first contradiction mapped back to the recipe's own Diet-category tag id (so
    /// the "Remove tag" action can target it), or <c>null</c> when the gate is off, nothing changed, or nothing
    /// contradicts. Never throws — the checker soft-fails to an empty list.
    /// </summary>
    public async Task<DietTagNudgeView?> EvaluateAsync(RecipeId recipeId, CancellationToken ct = default)
    {
        // Criterion 4: the toggle governs the call itself — no gate, no LLM cost.
        if (!await gate.IsEnabledAsync(ct)) return null;

        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return null;

        var dietTags = await DietTagsAsync(recipe, ct);
        if (dietTags.Count == 0) return null;

        // Defence in depth: an already-reconciled set never reaches the LLM even if the deferred load races a dismiss.
        if (recipe.CurrentIngredientProductHash() == recipe.DietNudgeDismissedHash) return null;

        var distinctIds = DistinctProductIds(recipe).ToList();
        var summaries = await products.ResolveSummariesAsync(distinctIds, ct);
        var ingredientNames = summaries.Values
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (ingredientNames.Count == 0) return null;

        var dietTagNames = dietTags.Select(t => t.Name).ToList();
        var contradictions = await checker.CheckAsync(ingredientNames, dietTagNames, ct);
        if (contradictions.Count == 0) return null;

        // One-line nudge: surface the first contradiction. Map its tag name back to the recipe's own Diet tag id
        // (case-insensitive) so "Remove tag" targets the right membership; fall back to the first diet tag if the
        // model echoed a name that no longer resolves.
        var first = contradictions[0];
        var tag = dietTags.FirstOrDefault(t => string.Equals(t.Name, first.DietTagName, StringComparison.OrdinalIgnoreCase))
                  ?? dietTags[0];
        return new DietTagNudgeView(recipe.Id.Value, first.IngredientName, tag.Id.Value, tag.Name);
    }

    /// <summary>
    /// "Keep it" — records the current ingredient set as reconciled so the nudge does not re-appear on the next
    /// save that leaves ingredients unchanged (acceptance criterion 1). No tag is touched. No-op when the recipe
    /// is gone.
    /// </summary>
    public async Task DismissAsync(RecipeId recipeId, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return;
        recipe.DismissDietNudge(clock);
        await recipes.SaveChangesAsync(ct);
        logger.LogInformation(
            "Diet-tag nudge kept (dismissed) for recipe {RecipeId}; ingredient set reconciled.", recipeId.Value);
    }

    /// <summary>
    /// "Remove &lt;tag&gt; tag" — the user acting on the nudge by dropping the contradicted Diet tag themselves
    /// (the AI never mutates the tag list, Gate 5 / C9). Removes only that tag and records the current ingredient
    /// set as reconciled. No-op when the recipe is gone or the tag is not applied.
    /// </summary>
    public async Task RemoveTagAsync(RecipeId recipeId, Guid tagId, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return;
        recipe.RemoveTag(TagId.From(tagId), clock);
        recipe.DismissDietNudge(clock);
        await recipes.SaveChangesAsync(ct);
        logger.LogInformation(
            "Diet-tag nudge: user removed tag {TagId} from recipe {RecipeId}; ingredient set reconciled.",
            tagId, recipeId.Value);
    }

    private static HashSet<Guid> DistinctProductIds(Recipe recipe) =>
        recipe.Ingredients.Select(i => i.ProductId).Distinct().ToHashSet();

    /// <summary>
    /// The recipe's applied tags that are Diet-category. Tags are a small per-household set, so one
    /// <see cref="ITagRepository.ListAllAsync"/> load (archived included, so a diet tag archived since the recipe
    /// was saved still counts) filtered to the recipe's membership is cheaper than per-id round-trips.
    /// </summary>
    private async Task<IReadOnlyList<Tag>> DietTagsAsync(Recipe recipe, CancellationToken ct)
    {
        var recipeTagIds = recipe.Tags.Select(rt => rt.TagId).ToHashSet();
        if (recipeTagIds.Count == 0) return [];
        var all = await tags.ListAllAsync(activeOnly: false, ct);
        return all.Where(t => recipeTagIds.Contains(t.Id) && t.Category == TagCategory.Diet).ToList();
    }
}
