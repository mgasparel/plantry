using System.Security.Cryptography;
using System.Text;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Aggregate root — the household's canonical definition of a dish (recipes-domain-model.md §3).
/// Owns its <see cref="Ingredient"/> collection (wholesale-replaced on each edit, O1), its tag
/// membership set (<see cref="RecipeTag"/>), and the optional 1:1 <see cref="RecipePhoto"/>.
/// Directions are a single text field — steps/sections are derived at render (Resolved call 4).
/// </summary>
public sealed class Recipe : AggregateRoot<RecipeId>
{
    public HouseholdId HouseholdId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Source { get; private set; }
    public int? CookTimeMinutes { get; private set; }
    public int DefaultServings { get; private set; }
    public string? Directions { get; private set; }

    /// <summary>Soft-delete marker (Resolved call 1); a recipe with cook history is never physically removed.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The <b>expanded</b> ProductId-set hash the user has already reconciled against this recipe's Diet-category
    /// tags — set when they dismiss ("Keep it") or act on ("Remove tag") the edit-moment diet-tag contradiction
    /// nudge (plantry-qll2.3, recipe-composition.md §8 / D9). Null until the nudge is first resolved. The nudge
    /// only re-appears once the distinct EXPANDED ProductId set (direct ingredients plus every nested inclusion's
    /// products) changes to something OTHER than this reconciled hash, so dismissing is remembered for that exact
    /// (recipe, expanded-set) and does not re-nag on a subsequent no-effective-change save.
    /// <para>The expanded set needs cross-aggregate reads (the included sub-recipes), so it is computed in the
    /// application layer via <c>RecipeExpansionService</c> and passed to <see cref="DismissDietNudge"/>; the
    /// in-aggregate <see cref="CurrentIngredientProductHash"/> still hashes only this recipe's DIRECT set and is
    /// used for the fast free path. Both use the same <see cref="IngredientProductHash"/> rule.</para>
    /// </summary>
    public string? DietNudgeDismissedHash { get; private set; }

    private readonly List<Ingredient> _ingredients = [];
    public IReadOnlyList<Ingredient> Ingredients => _ingredients.AsReadOnly();

    private readonly List<Inclusion> _inclusions = [];
    /// <summary>Included sub-recipes — a sibling line type next to <see cref="Ingredients"/> (D1).</summary>
    public IReadOnlyList<Inclusion> Inclusions => _inclusions.AsReadOnly();

    private readonly List<RecipeTag> _tags = [];
    public IReadOnlyList<RecipeTag> Tags => _tags.AsReadOnly();

    private RecipePhoto? _photo;
    public RecipePhoto? Photo => _photo;

    private Recipe() { } // EF

    // ── Factory ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new recipe. Validates name non-blank (R2) and defaultServings >= 1 (R2).
    /// Emits <see cref="RecipeCreatedEvent"/>.
    /// </summary>
    public static Result<Recipe> Create(
        HouseholdId householdId,
        string name,
        int defaultServings,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Custom("Recipes.InvalidName", "Recipe name must not be blank.");

        if (defaultServings < 1)
            return Error.Custom("Recipes.InvalidServings", "Default servings must be at least 1.");

        var now = clock.UtcNow;
        var recipe = new Recipe
        {
            Id = RecipeId.New(),
            HouseholdId = householdId,
            Name = name.Trim(),
            DefaultServings = defaultServings,
            CreatedAt = now,
            UpdatedAt = now,
        };

        recipe.RaiseDomainEvent(new RecipeCreatedEvent(recipe.Id, householdId, now));
        return recipe;
    }

    // ── Scalar mutators ────────────────────────────────────────────────────────

    /// <summary>Renames the recipe. Name uniqueness (R1) is enforced by the application layer.</summary>
    public Result Rename(string name, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Custom("Recipes.InvalidName", "Recipe name must not be blank.");

        Name = name.Trim();
        Touch(clock);
        return Result.Success();
    }

    public void SetSource(string? source, IClock clock)
    {
        Source = source;
        Touch(clock);
    }

    public void SetCookTime(int? cookTimeMinutes, IClock clock)
    {
        CookTimeMinutes = cookTimeMinutes;
        Touch(clock);
    }

    public void SetDirections(string? directions, IClock clock)
    {
        Directions = directions;
        Touch(clock);
    }

    // ── Tag membership ─────────────────────────────────────────────────────────

    /// <summary>Wholesale-replaces the recipe's tag set.</summary>
    public void SetTags(IReadOnlyList<TagId> tagIds, IClock clock)
    {
        _tags.Clear();
        foreach (var tagId in tagIds)
            _tags.Add(RecipeTag.Create(HouseholdId, Id, tagId));
        Touch(clock);
    }

    /// <summary>
    /// Removes a single tag from the recipe's membership set — the write behind the diet-tag contradiction
    /// nudge's "Remove &lt;tag&gt; tag" action (plantry-qll2.3), which the <b>user</b> triggers (the AI never
    /// mutates the tag list, Gate 5 / C9). No-op when the tag is not applied. Mirrors <see cref="SetTags"/> in
    /// not raising a <see cref="RecipeUpdatedEvent"/> — tag membership changes are not event-bearing.
    /// </summary>
    public void RemoveTag(TagId tagId, IClock clock)
    {
        var existing = _tags.FirstOrDefault(rt => rt.TagId == tagId);
        if (existing is null) return;
        _tags.Remove(existing);
        Touch(clock);
    }

    // ── Edit-moment diet-tag nudge reconciliation (plantry-qll2.3) ───────────────

    /// <summary>
    /// Records the recipe's current <b>expanded</b> product set as reconciled with its Diet-category tags —
    /// called when the user dismisses ("Keep it") or acts on ("Remove tag") the contradiction nudge. Stamps
    /// <see cref="DietNudgeDismissedHash"/> with <paramref name="expandedProductHash"/> (the
    /// <see cref="IngredientProductHash"/> of the fully expanded distinct ProductId set, computed by the
    /// application layer's <c>RecipeExpansionService</c> — the aggregate cannot read its included sub-recipes)
    /// so the same (recipe, expanded-set) never re-nags on a later no-effective-change save (D9).
    /// </summary>
    public void DismissDietNudge(string expandedProductHash, IClock clock)
    {
        DietNudgeDismissedHash = expandedProductHash;
        Touch(clock);
    }

    /// <summary>
    /// The order-independent hash of this recipe's DISTINCT ingredient ProductIds — the "did the ingredient set
    /// change" signal for the diet-tag nudge guard (plantry-qll2.3). Derived from the in-aggregate ProductIds
    /// alone (free — no cross-context name resolution), so most saves compute it without any Catalog round-trip.
    /// </summary>
    public string CurrentIngredientProductHash() =>
        IngredientProductHash(_ingredients.Select(i => i.ProductId));

    /// <summary>
    /// Deterministic, order-independent hash of a set of ingredient ProductIds: distinct + sorted, then a
    /// truncated SHA-256 hex digest. The empty set hashes to the empty string. Static so the application layer
    /// can hash a pre-save ProductId set with the exact same rule the aggregate uses.
    /// </summary>
    public static string IngredientProductHash(IEnumerable<Guid> productIds)
    {
        var sorted = productIds.Distinct().OrderBy(g => g).ToArray();
        if (sorted.Length == 0) return string.Empty;
        var joined = string.Join(",", sorted);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(digest)[..16];
    }

    // ── Photo management ───────────────────────────────────────────────────────

    /// <summary>
    /// Upserts the 1:1 photo. Updates in-place if a photo already exists (content/type/sha256),
    /// otherwise creates a new <see cref="RecipePhoto"/>.
    /// </summary>
    public void SetPhoto(byte[] content, string contentType, byte[]? sha256, IClock clock)
    {
        var now = clock.UtcNow;
        if (_photo is null)
        {
            _photo = RecipePhoto.Create(Id, HouseholdId, content, contentType, sha256, now);
        }
        else
        {
            _photo.Content = content;
            _photo.ContentType = contentType;
            _photo.Sha256 = sha256;
            _photo.UpdatedAt = now;
        }
        Touch(clock);
    }

    /// <summary>Removes the 1:1 photo if present; no-op if none exists.</summary>
    public void RemovePhoto(IClock clock)
    {
        _photo = null;
        Touch(clock);
    }

    // ── Line replacement (ingredients + inclusions) ────────────────────────────

    /// <summary>
    /// Wholesale-replaces the ingredient list with no inclusions — the backward-compatible entry point
    /// (delegates to <see cref="ReplaceLines"/> with an empty inclusion set). Retained so existing callers
    /// that only author ingredients keep compiling (recipe-composition.md §3 gotcha).
    /// </summary>
    public Result ReplaceIngredients(IReadOnlyList<IngredientLine> lines, IClock clock) =>
        ReplaceLines(lines, [], clock);

    /// <summary>
    /// Wholesale-replaces both line types (ingredients and inclusions) in a single operation, emitting one
    /// <see cref="RecipeUpdatedEvent"/> (recipe-composition.md §3). Enforces:
    /// R3′: at least one ingredient OR inclusion is required (D3);
    /// R4: every ingredient ProductId must be non-empty;
    /// R5: ingredient Quantity and UnitId must both be set or both null;
    /// N1: every inclusion Servings must be &gt; 0;
    /// N2: no inclusion may reference this recipe (no self-inclusion);
    /// N3 (R6 widened): ordinals must be contiguous across the UNION of ingredient and inclusion lines.
    /// Re-mints <see cref="IngredientId"/> and <see cref="InclusionId"/> per line (O1). The DAG / same-household
    /// / sub-existence checks (N4) are cross-aggregate and enforced by the application layer before this call.
    /// </summary>
    public Result ReplaceLines(
        IReadOnlyList<IngredientLine> ingredients,
        IReadOnlyList<InclusionLine> inclusions,
        IClock clock)
    {
        // R3′ — at least one ingredient OR inclusion
        if (ingredients.Count == 0 && inclusions.Count == 0)
            return Error.Custom("Recipes.NoIngredients",
                "A recipe must have at least one ingredient or included recipe.");

        // R4 — every ingredient ProductId non-null / non-empty
        foreach (var line in ingredients)
        {
            if (line.ProductId == Guid.Empty)
                return Error.Custom("Recipes.InvalidProductId", "Each ingredient must reference a product.");
        }

        // R5 — ingredient qty/unit both-set or both-null
        foreach (var line in ingredients)
        {
            if (line.Quantity.HasValue != line.UnitId.HasValue)
                return Error.Custom("Recipes.QtyUnitMismatch",
                    "Quantity and UnitId must both be set or both be null.");
        }

        // N1 — every inclusion serving count > 0
        foreach (var inc in inclusions)
        {
            if (inc.Servings <= 0)
                return Error.Custom("Recipes.InvalidInclusionServings",
                    "An included recipe must specify a positive number of servings.");
        }

        // N2 — no self-inclusion (the degenerate cycle)
        foreach (var inc in inclusions)
        {
            if (inc.SubRecipeId == Id)
                return Error.Custom("Recipes.SelfInclusion",
                    "A recipe cannot include itself.");
        }

        // N3 (R6 widened) — ordinals contiguous from min value across the union of BOTH line types
        var ordinals = ingredients.Select(l => l.Ordinal)
            .Concat(inclusions.Select(l => l.Ordinal))
            .OrderBy(o => o)
            .ToList();
        var minOrdinal = ordinals[0];
        for (var i = 0; i < ordinals.Count; i++)
        {
            if (ordinals[i] != minOrdinal + i)
                return Error.Custom("Recipes.NonContiguousOrdinals",
                    "Recipe line ordinals must be contiguous across ingredients and inclusions.");
        }

        _ingredients.Clear();
        foreach (var line in ingredients)
        {
            _ingredients.Add(Ingredient.Create(
                IngredientId.New(),
                HouseholdId,
                Id,
                line.ProductId,
                line.Quantity,
                line.UnitId,
                line.GroupHeading,
                line.Ordinal));
        }

        _inclusions.Clear();
        foreach (var inc in inclusions)
        {
            _inclusions.Add(Inclusion.Create(
                InclusionId.New(),
                HouseholdId,
                Id,
                inc.SubRecipeId,
                inc.Servings,
                inc.GroupHeading,
                inc.Ordinal));
        }

        Touch(clock);
        RaiseDomainEvent(new RecipeUpdatedEvent(Id, HouseholdId, UpdatedAt));
        return Result.Success();
    }

    // ── Serving mutation ───────────────────────────────────────────────────────

    /// <summary>
    /// Changes the default serving count. When <paramref name="mode"/> is
    /// <see cref="ScaleMode.Proportional"/>, ingredient quantities AND inclusion serving counts are
    /// multiplied by the new/old ratio (J7 step 3; recipe-composition.md D13 — a parent rescale scales all
    /// lines uniformly). Inclusion amounts are in servings of the sub, so scaling them by the parent ratio
    /// keeps the parent's per-serving composition constant. <see cref="ScaleMode.Keep"/> ("fixed") leaves
    /// both untouched. <paramref name="newServings"/> must be &gt;= 1.
    /// </summary>
    public Result ChangeDefaultServings(int newServings, ScaleMode mode, IClock clock)
    {
        if (newServings < 1)
            return Error.Custom("Recipes.InvalidServings", "Default servings must be at least 1.");

        if (mode == ScaleMode.Proportional)
        {
            var ratio = (decimal)newServings / DefaultServings;

            if (_ingredients.Count > 0)
            {
                // Rebuild ingredients with scaled quantities (re-mint ids per O1)
                var scaledIngredients = _ingredients.Select(ing => Ingredient.Create(
                    IngredientId.New(),
                    ing.HouseholdId,
                    ing.RecipeId,
                    ing.ProductId,
                    ing.Quantity.HasValue ? Math.Round(ing.Quantity.Value * ratio, 3) : null,
                    ing.UnitId,
                    ing.GroupHeading,
                    ing.Ordinal)).ToList();

                _ingredients.Clear();
                _ingredients.AddRange(scaledIngredients);
            }

            if (_inclusions.Count > 0)
            {
                // Rebuild inclusions with scaled serving counts (re-mint ids per O1)
                var scaledInclusions = _inclusions.Select(inc => Inclusion.Create(
                    InclusionId.New(),
                    inc.HouseholdId,
                    inc.RecipeId,
                    inc.SubRecipeId,
                    Math.Round(inc.Servings * ratio, 3),
                    inc.GroupHeading,
                    inc.Ordinal)).ToList();

                _inclusions.Clear();
                _inclusions.AddRange(scaledInclusions);
            }
        }

        DefaultServings = newServings;
        Touch(clock);
        return Result.Success();
    }

    // ── Archival (soft delete) ─────────────────────────────────────────────────

    /// <summary>
    /// Soft-deletes the recipe by stamping <see cref="ArchivedAt"/> (Resolved call 1). Idempotent — a
    /// re-archive keeps the original timestamp. The N5 guard — blocking archival while the recipe is
    /// referenced by another recipe's inclusion — is cross-aggregate and enforced by the application layer
    /// (recipe-composition.md D12/N5) before this call.
    /// </summary>
    public void Archive(IClock clock)
    {
        if (ArchivedAt is not null) return;
        ArchivedAt = clock.UtcNow;
        Touch(clock);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private void Touch(IClock clock) => UpdatedAt = clock.UtcNow;
}
