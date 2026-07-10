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
/// A single reverse-ripple diet-tag nudge (recipe-composition.md D10): surfaced on a saved SUB-recipe's landing
/// for one including PARENT whose <b>expanded</b> product set now contradicts its Diet tag ("may conflict with
/// 'Vegan' on Nachos"). Carries the parent's id + display name (the landing is the sub's page, so the parent must
/// be named), the offending ingredient, and the parent's own contradicted Diet tag (id + name) — the "Keep it" /
/// "Remove tag" actions and the dismissal hash all target the PARENT, identical to the direct nudge but per-parent.
/// </summary>
public sealed record DietTagRippleNudgeView(
    Guid ParentRecipeId, string ParentName, string IngredientName, Guid DietTagId, string DietTagName);

/// <summary>
/// Application service behind the recipe editor's edit-moment diet-tag contradiction nudge (plantry-qll2.3).
/// It keeps the two-stage guard the epic (plantry-qll2) design calls for in one place:
/// <list type="number">
///   <item><see cref="ShouldOfferAfterSaveAsync"/> — the <b>cheap</b> post-save trigger the editor POST runs to
///   decide whether to defer a check at all. It fires only when the <b>expanded</b> ProductId set (direct
///   ingredients plus every nested inclusion's products, D9) actually changed AND the recipe carries a
///   Diet-category tag AND that new set has not already been reconciled. No LLM, no Catalog name resolution — the
///   ProductId-set hash comes from one recursive repo walk of the included recipes (cheap). This is what keeps
///   "most saves trigger nothing" true and confines the AI strictly to the edit moment (never a corpus sweep).</item>
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
    RecipeExpansionService expansion,
    ITagRepository tags,
    ICatalogProductReader products,
    IAiAssistanceGateReader gate,
    IDietTagContradictionChecker checker,
    IClock clock,
    ILogger<DietTagNudgeService> logger)
{
    /// <summary>
    /// Cheap, no-LLM post-save trigger guard. Returns true only when a deferred contradiction check is warranted:
    /// the recipe still exists, its distinct <b>expanded</b> ProductId set — direct ingredients plus every nested
    /// inclusion's products (D9) — differs from <paramref name="previousProductIds"/> (the expanded set BEFORE this
    /// save, so an effective-set-neutral edit changes nothing here, acceptance criterion 2, and editing which
    /// recipes are included re-triggers), it carries at least one Diet-category tag (criterion 3), and the new set
    /// has not already been dismissed for this recipe. Computing the expanded set adds ONE recursive repo walk of
    /// the included recipes — still no LLM and no Catalog name resolution, so "most saves trigger nothing" survives.
    /// The household assistive-AI gate is deliberately checked later, in <see cref="EvaluateAsync"/>, so the toggle
    /// still governs the LLM call itself (criterion 4) without a gate round-trip on every save. A recipe whose
    /// expansion cannot be resolved (missing sub) never nags — the advisory check fails safe.
    /// </summary>
    public async Task<bool> ShouldOfferAfterSaveAsync(
        RecipeId recipeId, IReadOnlySet<Guid> previousProductIds, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return false;

        var expanded = await expansion.ExpandedProductIdsAsync(recipeId, ct);
        if (expanded.IsFailure) return false; // advisory: an unresolvable expansion never nags

        var currentIds = expanded.Value;
        if (currentIds.SetEquals(previousProductIds)) return false; // expanded set unchanged (criterion 2)

        var dietTags = await DietTagsAsync(recipe, ct);
        if (dietTags.Count == 0) return false; // no Diet-category tag (criterion 3)

        // Already reconciled for this exact EXPANDED set — don't re-nag (criterion 1, "dismiss remembered").
        return Recipe.IngredientProductHash(currentIds) != recipe.DietNudgeDismissedHash;
    }

    /// <summary>
    /// Reverse-ripple guard (recipe-composition.md §8 / D10) — the cheap, no-LLM post-save trigger the editor POST
    /// runs after saving <paramref name="savedRecipeId"/>. Saving a recipe changes the <b>expanded</b> product set
    /// of every recipe that INCLUDES it, with no parent save to fire the direct nudge — so this reverse-looks-up the
    /// transitively-including parents (<see cref="IRecipeRepository.GetIncluderIdsAsync"/>, transitive) and returns
    /// the ids of those that (a) carry at least one Diet-category tag and (b) whose current expanded-set hash differs
    /// from their own <see cref="Recipe.DietNudgeDismissedHash"/> — i.e. unchanged-or-already-reconciled parents are
    /// skipped, per-parent. Ordered by parent name for a stable landing render. No LLM and no Catalog name
    /// resolution here (those live in the deferred <see cref="EvaluateRippleAsync"/>); expansion is computed only for
    /// the diet-tagged includers, so a save whose recipe has no includers — or no diet-tagged includers — does no
    /// expansion and no LLM work beyond the includers lookup (acceptance criterion 4). A parent whose expansion
    /// cannot be resolved (missing sub) never nags — the advisory check fails safe.
    /// </summary>
    public async Task<IReadOnlyList<RecipeId>> IncludersNeedingRippleNudgeAsync(
        RecipeId savedRecipeId, CancellationToken ct = default)
    {
        var includerIds = await recipes.GetIncluderIdsAsync(savedRecipeId, transitive: true, ct);
        if (includerIds.Count == 0) return []; // no includers — nothing beyond the lookup (criterion 4)

        // Diet-tag vocabulary loaded ONCE (a small per-household set; archived included so a diet tag archived since
        // the parent was saved still counts) so classifying includers costs no per-parent tag round-trip.
        var allTags = await tags.ListAllAsync(activeOnly: false, ct);
        var dietTagIds = allTags.Where(t => t.Category == TagCategory.Diet).Select(t => t.Id).ToHashSet();

        var matches = new List<Recipe>();
        foreach (var parentId in includerIds)
        {
            var parent = await recipes.GetByIdAsync(parentId, ct);
            if (parent is null) continue;

            // Not diet-tagged — skip BEFORE any expansion, so a non-diet includer costs no expensive work (criterion 4).
            if (!parent.Tags.Any(rt => dietTagIds.Contains(rt.TagId))) continue;

            var expanded = await expansion.ExpandedProductIdsAsync(parentId, ct);
            if (expanded.IsFailure) continue; // advisory: an unresolvable expansion never nags

            // Unchanged or already reconciled for this exact expanded set — skip (per-parent dismissal, D10).
            if (Recipe.IngredientProductHash(expanded.Value) == parent.DietNudgeDismissedHash) continue;

            matches.Add(parent);
        }

        return matches
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// The deferred per-parent ripple check (D10): runs the very same assistive-AI gate + expanded-set name
    /// resolution + untrusted LLM check as <see cref="EvaluateAsync"/> for an including <paramref name="parentRecipeId"/>,
    /// then wraps the result with the parent's display name so the sub's save landing can name the conflict ("may
    /// conflict with 'Vegan' on Nachos"). Returns <c>null</c> whenever the direct check would (gate off, parent gone,
    /// expanded set already reconciled, or nothing contradicts). The mapped tag id, the "Remove tag" action, and the
    /// dismissal hash all target the PARENT — dismissal/gate semantics are identical to the direct nudge, per-parent.
    /// </summary>
    public async Task<DietTagRippleNudgeView?> EvaluateRippleAsync(
        RecipeId parentRecipeId, CancellationToken ct = default)
    {
        var view = await EvaluateAsync(parentRecipeId, ct);
        if (view is null) return null;

        var parent = await recipes.GetByIdAsync(parentRecipeId, ct);
        if (parent is null) return null; // raced deletion — nothing to name

        return new DietTagRippleNudgeView(
            view.RecipeId, parent.Name, view.IngredientName, view.DietTagId, view.DietTagName);
    }

    /// <summary>
    /// The deferred check: re-verifies the guard, applies the assistive-AI gate, resolves the <b>expanded</b>
    /// ingredient names via the Catalog ACL (<c>Ingredient</c> has no Name — cross-context by construction) so a
    /// dairy product inside a sub-recipe of a Vegan-tagged parent is catchable (D9), and asks the untrusted checker
    /// for contradictions. Returns the first contradiction mapped back to the recipe's own Diet-category tag id (so
    /// the "Remove tag" action can target it), or <c>null</c> when the gate is off, nothing changed, the expansion
    /// cannot be resolved, or nothing contradicts. Never throws — the checker soft-fails to an empty list.
    /// </summary>
    public async Task<DietTagNudgeView?> EvaluateAsync(RecipeId recipeId, CancellationToken ct = default)
    {
        // Criterion 4: the toggle governs the call itself — no gate, no LLM cost.
        if (!await gate.IsEnabledAsync(ct)) return null;

        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return null;

        var dietTags = await DietTagsAsync(recipe, ct);
        if (dietTags.Count == 0) return null;

        var expanded = await expansion.ExpandedProductIdsAsync(recipeId, ct);
        if (expanded.IsFailure) return null; // unresolvable expansion — nothing to check

        // Defence in depth: an already-reconciled EXPANDED set never reaches the LLM even if the deferred load
        // races a dismiss.
        if (Recipe.IngredientProductHash(expanded.Value) == recipe.DietNudgeDismissedHash) return null;

        var distinctIds = expanded.Value.ToList();
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
        recipe.DismissDietNudge(await ExpandedProductHashAsync(recipe, ct), clock);
        await recipes.SaveChangesAsync(ct);
        logger.LogInformation(
            "Diet-tag nudge kept (dismissed) for recipe {RecipeId}; expanded ingredient set reconciled.", recipeId.Value);
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
        recipe.DismissDietNudge(await ExpandedProductHashAsync(recipe, ct), clock);
        await recipes.SaveChangesAsync(ct);
        logger.LogInformation(
            "Diet-tag nudge: user removed tag {TagId} from recipe {RecipeId}; expanded ingredient set reconciled.",
            tagId, recipeId.Value);
    }

    /// <summary>
    /// The <see cref="Recipe.IngredientProductHash"/> of the recipe's fully expanded distinct ProductId set (D9),
    /// used to stamp <see cref="Recipe.DietNudgeDismissedHash"/> on reconciliation. If the expansion cannot be
    /// resolved (missing sub), falls back to the aggregate's direct-set hash so a dismiss still records something
    /// deterministic — the guard already refuses to nag on an unresolvable expansion, so this fallback is defensive.
    /// </summary>
    private async Task<string> ExpandedProductHashAsync(Recipe recipe, CancellationToken ct)
    {
        var expanded = await expansion.ExpandedProductIdsAsync(recipe.Id, ct);
        return expanded.IsSuccess
            ? Recipe.IngredientProductHash(expanded.Value)
            : recipe.CurrentIngredientProductHash();
    }

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
