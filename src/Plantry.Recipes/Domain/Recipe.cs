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

    private readonly List<Ingredient> _ingredients = [];
    public IReadOnlyList<Ingredient> Ingredients => _ingredients.AsReadOnly();

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

    // ── Ingredient replacement ─────────────────────────────────────────────────

    /// <summary>
    /// Wholesale-replaces the ingredient list. Enforces:
    /// R3: at least one ingredient required;
    /// R4: every ProductId must be non-empty;
    /// R5: Quantity and UnitId must both be set or both null;
    /// R6: ordinals must be contiguous from the minimum value.
    /// Re-mints IngredientId per line (O1). Emits <see cref="RecipeUpdatedEvent"/>.
    /// </summary>
    public Result ReplaceIngredients(IReadOnlyList<IngredientLine> lines, IClock clock)
    {
        // R3 — at least one ingredient
        if (lines.Count == 0)
            return Error.Custom("Recipes.NoIngredients", "A recipe must have at least one ingredient.");

        // R4 — every ProductId non-null / non-empty
        foreach (var line in lines)
        {
            if (line.ProductId == Guid.Empty)
                return Error.Custom("Recipes.InvalidProductId", "Each ingredient must reference a product.");
        }

        // R5 — qty/unit both-set or both-null
        foreach (var line in lines)
        {
            if (line.Quantity.HasValue != line.UnitId.HasValue)
                return Error.Custom("Recipes.QtyUnitMismatch",
                    "Quantity and UnitId must both be set or both be null.");
        }

        // R6 — ordinals contiguous from min value (0 or 1 per issue)
        var ordinals = lines.Select(l => l.Ordinal).OrderBy(o => o).ToList();
        var minOrdinal = ordinals[0];
        for (var i = 0; i < ordinals.Count; i++)
        {
            if (ordinals[i] != minOrdinal + i)
                return Error.Custom("Recipes.NonContiguousOrdinals",
                    "Ingredient ordinals must be contiguous.");
        }

        _ingredients.Clear();
        foreach (var line in lines)
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

        Touch(clock);
        RaiseDomainEvent(new RecipeUpdatedEvent(Id, HouseholdId, UpdatedAt));
        return Result.Success();
    }

    // ── Serving mutation ───────────────────────────────────────────────────────

    /// <summary>
    /// Changes the default serving count. When <paramref name="mode"/> is
    /// <see cref="ScaleMode.Proportional"/>, ingredient quantities are multiplied by
    /// new/old ratio (J7 step 3). <paramref name="newServings"/> must be >= 1.
    /// </summary>
    public Result ChangeDefaultServings(int newServings, ScaleMode mode, IClock clock)
    {
        if (newServings < 1)
            return Error.Custom("Recipes.InvalidServings", "Default servings must be at least 1.");

        if (mode == ScaleMode.Proportional && _ingredients.Count > 0)
        {
            var ratio = (decimal)newServings / DefaultServings;
            // Rebuild ingredients with scaled quantities (re-mint ids per O1)
            var scaled = _ingredients.Select(ing => Ingredient.Create(
                IngredientId.New(),
                ing.HouseholdId,
                ing.RecipeId,
                ing.ProductId,
                ing.Quantity.HasValue ? Math.Round(ing.Quantity.Value * ratio, 3) : null,
                ing.UnitId,
                ing.GroupHeading,
                ing.Ordinal)).ToList();

            _ingredients.Clear();
            _ingredients.AddRange(scaled);
        }

        DefaultServings = newServings;
        Touch(clock);
        return Result.Success();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private void Touch(IClock clock) => UpdatedAt = clock.UtcNow;
}
