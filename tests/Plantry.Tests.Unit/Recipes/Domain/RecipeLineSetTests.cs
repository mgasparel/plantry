using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// Direct, pure tests for the recipe line-set invariant matrix — R3′/R4/R5/N1/N2/N3 — now that validation
/// lives in <see cref="RecipeLineSet.Create"/> rather than inline in the aggregate. Each invariant has a
/// reject case and (where meaningful) an accept case, exercised without constructing a <see cref="Recipe"/>.
/// </summary>
public sealed class RecipeLineSetTests
{
    private static readonly RecipeId OwningRecipe = RecipeId.New();

    private static IngredientLine Ingredient(
        Guid? productId = null, decimal? qty = 1m, Guid? unitId = null, int ordinal = 0) =>
        new(productId ?? Guid.CreateVersion7(), qty, unitId ?? Guid.CreateVersion7(), null, ordinal);

    private static InclusionLine Inclusion(
        RecipeId? subId = null, decimal servings = 2m, int ordinal = 0) =>
        new(subId ?? RecipeId.New(), servings, null, ordinal);

    private static Result<RecipeLineSet> Create(
        IReadOnlyList<IngredientLine>? ingredients = null,
        IReadOnlyList<InclusionLine>? inclusions = null,
        RecipeId? owningRecipeId = null) =>
        RecipeLineSet.Create(ingredients ?? [], inclusions ?? [], owningRecipeId ?? OwningRecipe);

    // ── Success ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Succeeds_With_Ingredients_Only()
    {
        var result = Create(ingredients: [Ingredient()]);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Ingredients);
        Assert.Empty(result.Value.Inclusions);
    }

    [Fact]
    public void Create_Succeeds_With_Inclusions_Only_R3Prime()
    {
        var result = Create(inclusions: [Inclusion()]);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Ingredients);
        Assert.Single(result.Value.Inclusions);
    }

    [Fact]
    public void Create_Succeeds_With_Mixed_Lines()
    {
        var result = Create([Ingredient(ordinal: 0)], [Inclusion(ordinal: 1)]);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Ingredients);
        Assert.Single(result.Value.Inclusions);
    }

    [Fact]
    public void Create_Preserves_The_Supplied_Lines()
    {
        var ingredient = Ingredient(ordinal: 0);
        var inclusion = Inclusion(ordinal: 1);

        var set = Create([ingredient], [inclusion]).Value;

        Assert.Same(ingredient, Assert.Single(set.Ingredients));
        Assert.Same(inclusion, Assert.Single(set.Inclusions));
    }

    // ── R3′ — at least one line ──────────────────────────────────────────────────

    [Fact]
    public void Create_R3Prime_Rejects_Both_Empty()
    {
        var result = Create();

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NoIngredients", result.Error.Code);
    }

    // ── R4 — ingredient product non-empty ────────────────────────────────────────

    [Fact]
    public void Create_R4_Rejects_Empty_ProductId()
    {
        var result = Create(ingredients: [Ingredient(productId: Guid.Empty)]);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidProductId", result.Error.Code);
    }

    // ── R5 — qty/unit both-set or both-null ──────────────────────────────────────

    [Fact]
    public void Create_R5_Rejects_Qty_Without_Unit()
    {
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 500m, null, null, 0) };

        var result = Create(ingredients: lines);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.QtyUnitMismatch", result.Error.Code);
    }

    [Fact]
    public void Create_R5_Rejects_Unit_Without_Qty()
    {
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), null, Guid.CreateVersion7(), null, 0) };

        var result = Create(ingredients: lines);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.QtyUnitMismatch", result.Error.Code);
    }

    [Fact]
    public void Create_R5_Allows_Both_Null()
    {
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), null, null, null, 0) };

        var result = Create(ingredients: lines);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Ingredients[0].Quantity);
        Assert.Null(result.Value.Ingredients[0].UnitId);
    }

    // ── N1 — inclusion servings > 0 ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_N1_Rejects_NonPositive_Servings(int servings)
    {
        var result = Create(inclusions: [Inclusion(servings: servings)]);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidInclusionServings", result.Error.Code);
    }

    // ── N2 — no self-inclusion ───────────────────────────────────────────────────

    [Fact]
    public void Create_N2_Rejects_Self_Inclusion()
    {
        var result = Create(inclusions: [Inclusion(subId: OwningRecipe)], owningRecipeId: OwningRecipe);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.SelfInclusion", result.Error.Code);
    }

    [Fact]
    public void Create_N2_Allows_Inclusion_Of_A_Different_Recipe()
    {
        var result = Create(inclusions: [Inclusion(subId: RecipeId.New())], owningRecipeId: OwningRecipe);

        Assert.True(result.IsSuccess);
    }

    // ── N3 (R6 widened) — contiguous ordinals across the union ────────────────────

    [Fact]
    public void Create_N3_Rejects_NonContiguous_Ingredient_Ordinals()
    {
        var lines = new[]
        {
            Ingredient(ordinal: 0),
            Ingredient(ordinal: 2), // gap at 1
        };

        var result = Create(ingredients: lines);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NonContiguousOrdinals", result.Error.Code);
    }

    [Fact]
    public void Create_N3_Rejects_NonContiguous_Ordinals_Across_Union()
    {
        // Ingredient at 0, inclusion at 2 — gap at 1 across the union.
        var result = Create([Ingredient(ordinal: 0)], [Inclusion(ordinal: 2)]);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NonContiguousOrdinals", result.Error.Code);
    }

    [Fact]
    public void Create_N3_Accepts_Contiguous_Ordinals_Across_Union()
    {
        var result = Create([Ingredient(ordinal: 0)], [Inclusion(ordinal: 1)]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Create_N3_Allows_Ordinals_Starting_From_One()
    {
        var lines = new[]
        {
            Ingredient(ordinal: 1),
            Ingredient(ordinal: 2),
        };

        var result = Create(ingredients: lines);

        Assert.True(result.IsSuccess);
    }
}
