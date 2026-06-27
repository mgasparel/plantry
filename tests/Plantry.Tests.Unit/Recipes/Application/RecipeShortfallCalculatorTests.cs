using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// Direct unit tests for <see cref="RecipeShortfallCalculator"/>.
///
/// Covers:
/// <list type="bullet">
///   <item>Missing line → shortfall equals full scaled required quantity (available = 0).</item>
///   <item>Low line → shortfall equals required − available (deficit only).</item>
///   <item>InStock line → excluded (shortfall would be 0 or negative).</item>
///   <item>Untracked line → excluded (C12).</item>
///   <item>Null Quantity/UnitId → skipped (untracked staple / malformed, R5).</item>
///   <item>Scaling is applied correctly (desiredServings / defaultServings).</item>
/// </list>
/// </summary>
public sealed class RecipeShortfallCalculatorTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();

    // ── Missing → full scaled required ──────────────────────────────────────────

    [Fact(DisplayName = "Missing line emits shortfall equal to full scaled required quantity")]
    public void Missing_Line_Emits_Full_Required()
    {
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        var recipe = BuildRecipe(defaultServings: 4, productId, qty: 200m, unitId);
        var ingredientId = recipe.Ingredients.Single().Id;

        // Missing = no available stock
        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.Missing, null, null),
        ]);

        // Scale: 4/4 = 1 → shortfall = 200
        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 4);

        var line = Assert.Single(result);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(200m, line.ShortfallQuantity);
        Assert.Equal(unitId, line.UnitId);
    }

    // ── Low → deficit only (required − available) ───────────────────────────────

    [Fact(DisplayName = "Low line emits shortfall equal to required minus available")]
    public void Low_Line_Emits_Deficit()
    {
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        var recipe = BuildRecipe(defaultServings: 4, productId, qty: 100m, unitId);
        var ingredientId = recipe.Ingredients.Single().Id;

        // Low: 30 available, need 100 → shortfall = 70
        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.Low, null, AvailableQuantity: 30m),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 4);

        var line = Assert.Single(result);
        Assert.Equal(70m, line.ShortfallQuantity); // 100 - 30 = 70
    }

    // ── InStock → excluded ───────────────────────────────────────────────────────

    [Fact(DisplayName = "InStock line is excluded from shortfall output")]
    public void InStock_Line_Is_Excluded()
    {
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        var recipe = BuildRecipe(defaultServings: 2, productId, qty: 100m, unitId);
        var ingredientId = recipe.Ingredients.Single().Id;

        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.InStock, null, AvailableQuantity: 200m),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 2);

        Assert.Empty(result);
    }

    // ── Untracked → excluded (C12) ──────────────────────────────────────────────

    [Fact(DisplayName = "Untracked line is excluded from shortfall output (C12)")]
    public void Untracked_Line_Is_Excluded()
    {
        var productId = Guid.CreateVersion7();

        // Untracked staple: null Quantity and UnitId
        var recipe = Recipe.Create(Household, "Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0)],
            Clock);
        var ingredientId = recipe.Ingredients.Single().Id;

        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.Untracked, null, null),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 2);

        Assert.Empty(result);
    }

    // ── Null Quantity/UnitId skipped even if status is Missing ──────────────────

    [Fact(DisplayName = "Ingredient with null Quantity/UnitId is skipped even if status is Missing (defensive guard)")]
    public void NullQuantityOrUnit_IsSkipped()
    {
        var productId = Guid.CreateVersion7();

        var recipe = Recipe.Create(Household, "Recipe", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0)],
            Clock);
        var ingredientId = recipe.Ingredients.Single().Id;

        // Fabricate a Missing line even though FulfillmentService would normally give Untracked
        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.Missing, null, null),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 2);

        Assert.Empty(result);
    }

    // ── Scaling applied ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Scaling is applied: shortfall = (required × scale) − available")]
    public void Scaling_Applied_To_Shortfall()
    {
        var unitId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        // Default 4 servings, ingredient qty 100. Ask for 6 → scale 1.5 → required = 150.
        // Low: 40 available → shortfall = 110.
        var recipe = BuildRecipe(defaultServings: 4, productId, qty: 100m, unitId);
        var ingredientId = recipe.Ingredients.Single().Id;

        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredientId, IngredientStatus.Low, null, AvailableQuantity: 40m),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 6);

        var line = Assert.Single(result);
        Assert.Equal(110m, line.ShortfallQuantity); // (100 × 1.5) - 40 = 110
    }

    // ── Mixed: Missing, Low, InStock, Untracked ─────────────────────────────────

    [Fact(DisplayName = "Mixed recipe: Missing and Low emitted, InStock and Untracked excluded")]
    public void Mixed_Statuses_Only_Missing_And_Low_Emitted()
    {
        var unitId = Guid.CreateVersion7();
        var missingId = Guid.CreateVersion7();
        var lowId = Guid.CreateVersion7();
        var inStockId = Guid.CreateVersion7();
        var untrackedId = Guid.CreateVersion7();

        var recipe = Recipe.Create(Household, "Mixed", 2, Clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(missingId, 100m, unitId, null, 0),
            new IngredientLine(lowId, 80m, unitId, null, 1),
            new IngredientLine(inStockId, 50m, unitId, null, 2),
            new IngredientLine(untrackedId, null, null, null, 3),
        ], Clock);

        var ingredients = recipe.Ingredients.OrderBy(i => i.Ordinal).ToList();

        var fulfillment = BuildFulfillment([
            new IngredientFulfillment(ingredients[0].Id, IngredientStatus.Missing, null, null),
            new IngredientFulfillment(ingredients[1].Id, IngredientStatus.Low, null, AvailableQuantity: 20m),
            new IngredientFulfillment(ingredients[2].Id, IngredientStatus.InStock, null, AvailableQuantity: 60m),
            new IngredientFulfillment(ingredients[3].Id, IngredientStatus.Untracked, null, null),
        ]);

        var result = RecipeShortfallCalculator.Compute(recipe, fulfillment, desiredServings: 2);

        Assert.Equal(2, result.Count); // Missing + Low only

        var missingLine = result.Single(r => r.ProductId == missingId);
        Assert.Equal(100m, missingLine.ShortfallQuantity); // full required

        var lowLine = result.Single(r => r.ProductId == lowId);
        Assert.Equal(60m, lowLine.ShortfallQuantity); // 80 - 20 = 60
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Recipe BuildRecipe(int defaultServings, Guid productId, decimal qty, Guid unitId)
    {
        var recipe = Recipe.Create(Household, "Test Recipe", defaultServings, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(productId, qty, unitId, null, 0)],
            Clock);
        return recipe;
    }

    private static FulfillmentResult BuildFulfillment(IReadOnlyList<IngredientFulfillment> lines)
    {
        var missing = lines.Count(l => l.Status == IngredientStatus.Missing);
        var low = lines.Count(l => l.Status == IngredientStatus.Low);
        var overall = new FulfillmentOverall(
            FullyCookable: missing == 0 && low == 0,
            MissingCount: missing,
            LowCount: low);
        return new FulfillmentResult(overall, lines);
    }
}
