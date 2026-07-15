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

    // ── Yield-on-cook (plantry-854a, recipe-composition.md §9) ───────────────────
    // A recipe MAY declare a yield: cooking it can add leftover/prepped servings to inventory as a
    // catalog product. The three fields form a tri-state — all set together or all null (Y1). When set,
    // YieldProductId is an ordinary tracked catalog product (auto-creatable from the recipe name at the
    // Web/application boundary), YieldQuantity (> 0, Y2) is the declared yield for the recipe's default
    // servings (a hint that pre-fills the cook-time "storing N" field), and YieldUnitId is a
    // servings-like count unit by default (a real unit — cups — is allowed for prep recipes). All are
    // bare soft-refs (DM-3) — no FK to catalog.

    /// <summary>The catalog product a cook of this recipe can store as yield; null when no yield is declared.</summary>
    public Guid? YieldProductId { get; private set; }

    /// <summary>The declared yield quantity for <see cref="DefaultServings"/>; &gt; 0 when set, null otherwise.</summary>
    public decimal? YieldQuantity { get; private set; }

    /// <summary>Unit of <see cref="YieldQuantity"/> — a servings-like count unit by default; null when no yield.</summary>
    public Guid? YieldUnitId { get; private set; }

    /// <summary>True when this recipe declares a yield (all three yield fields are set).</summary>
    public bool HasYield => YieldProductId is not null;

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

    /// <summary>
    /// Sets or clears the recipe's yield declaration (plantry-854a, recipe-composition.md §9). The three
    /// arguments are a tri-state: pass all three to declare/edit a yield, or all null to remove it (Y1). A
    /// declared yield requires a non-empty product, a positive quantity (Y2), and a unit; a partial set is
    /// rejected. The yield product is expected to be a tracked catalog product — the application layer
    /// auto-creates it from the recipe name when the author first enables the yield, and passes its id here.
    /// </summary>
    public Result SetYield(Guid? productId, decimal? quantity, Guid? unitId, IClock clock)
    {
        var anySet = productId is not null || quantity is not null || unitId is not null;
        if (anySet)
        {
            if (productId is null || productId.Value == Guid.Empty)
                return Error.Custom("Recipes.InvalidYieldProduct", "A yield must reference a product.");
            if (quantity is null || quantity.Value <= 0m)
                return Error.Custom("Recipes.InvalidYieldQuantity", "Yield quantity must be greater than zero.");
            if (unitId is null || unitId.Value == Guid.Empty)
                return Error.Custom("Recipes.InvalidYieldUnit", "A yield must specify a unit.");
        }

        YieldProductId = productId;
        YieldQuantity = quantity;
        YieldUnitId = unitId;
        Touch(clock);
        return Result.Success();
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
    /// Wholesale-replaces the ingredient list with no inclusions — an ingredients-only convenience over
    /// <see cref="ReplaceLines"/>. Builds and validates a <see cref="RecipeLineSet"/> (empty inclusion set)
    /// through the single validation home, so the line-set invariants (R3′/R4/R5) are enforced here too;
    /// returns the first blocking <see cref="Error"/> when they fail. Retained so ingredient-only callers
    /// keep a one-line entry point (recipe-composition.md §3 gotcha).
    /// </summary>
    public Result ReplaceIngredients(IReadOnlyList<IngredientLine> lines, IClock clock)
    {
        var lineSet = RecipeLineSet.Create(lines, [], Id);
        if (lineSet.IsFailure)
            return lineSet.Error;

        ReplaceLines(lineSet.Value, clock);
        return Result.Success();
    }

    /// <summary>
    /// Wholesale-replaces both line types (ingredients and inclusions) from an already-validated
    /// <see cref="RecipeLineSet"/>, emitting one <see cref="RecipeUpdatedEvent"/> (recipe-composition.md §3).
    /// The line-set invariants (R3′/R4/R5/N1/N2/N3) are enforced once, at construction, by
    /// <see cref="RecipeLineSet.Create"/> — this method carries no validation and cannot fail, so it simply
    /// applies the set, re-minting <see cref="IngredientId"/> and <see cref="InclusionId"/> per line (O1),
    /// touches the aggregate, and raises the update event. The DAG / same-household / sub-existence checks
    /// (N4) are cross-aggregate and enforced by the application layer before the set is built.
    /// </summary>
    public void ReplaceLines(RecipeLineSet lineSet, IClock clock)
    {
        _ingredients.Clear();
        foreach (var line in lineSet.Ingredients)
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
        foreach (var inc in lineSet.Inclusions)
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
