using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

public sealed class RecipeTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // ── Helper factories ──────────────────────────────────────────────────────

    private static Recipe NewRecipe(string name = "Pasta Bolognese", int servings = 4) =>
        Recipe.Create(Household, name, servings, Clock).Value;

    private static IReadOnlyList<IngredientLine> OneIngredient(
        Guid? productId = null,
        decimal? qty = 500m,
        Guid? unitId = null) =>
        [new IngredientLine(productId ?? Guid.CreateVersion7(), qty, unitId ?? Guid.CreateVersion7(), null, 0)];

    private static IReadOnlyList<IngredientLine> ThreeIngredients() =>
    [
        new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0),
        new IngredientLine(Guid.CreateVersion7(), 100m, Guid.CreateVersion7(), "Main sauce", 1),
        new IngredientLine(Guid.CreateVersion7(), null, null, null, 2),
    ];

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Sets_Properties_And_Emits_RecipeCreated()
    {
        var result = Recipe.Create(Household, "  Pasta  ", 2, Clock);

        Assert.True(result.IsSuccess);
        var recipe = result.Value;
        Assert.Equal(Household, recipe.HouseholdId);
        Assert.Equal("Pasta", recipe.Name); // trimmed
        Assert.Equal(2, recipe.DefaultServings);
        Assert.Equal(recipe.CreatedAt, recipe.UpdatedAt);
        Assert.NotEqual(Guid.Empty, recipe.Id.Value);

        var evt = Assert.Single(recipe.DomainEvents);
        var created = Assert.IsType<RecipeCreatedEvent>(evt);
        Assert.Equal(recipe.Id, created.RecipeId);
        Assert.Equal(Household, created.HouseholdId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        var result = Recipe.Create(Household, name, 4, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidName", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_Rejects_Servings_Below_One(int servings)
    {
        var result = Recipe.Create(Household, "Soup", servings, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidServings", result.Error.Code);
    }

    [Fact]
    public void Create_With_One_Serving_Succeeds()
    {
        var result = Recipe.Create(Household, "Soup", 1, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.DefaultServings);
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_Updates_Name_And_Touches_UpdatedAt()
    {
        var recipe = NewRecipe();
        var later = new FixedClock(Later);

        var result = recipe.Rename("  Spaghetti Carbonara  ", later);

        Assert.True(result.IsSuccess);
        Assert.Equal("Spaghetti Carbonara", recipe.Name);
        Assert.Equal(Later, recipe.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_Rejects_Blank_Name(string name)
    {
        var recipe = NewRecipe();

        var result = recipe.Rename(name, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidName", result.Error.Code);
    }

    // ── Scalar mutators ────────────────────────────────────────────────────────

    [Fact]
    public void SetSource_Updates_Source_And_Touches_UpdatedAt()
    {
        var recipe = NewRecipe();
        var later = new FixedClock(Later);

        recipe.SetSource("https://example.com/recipe", later);

        Assert.Equal("https://example.com/recipe", recipe.Source);
        Assert.Equal(Later, recipe.UpdatedAt);
    }

    [Fact]
    public void SetCookTime_Updates_CookTimeMinutes()
    {
        var recipe = NewRecipe();

        recipe.SetCookTime(30, Clock);

        Assert.Equal(30, recipe.CookTimeMinutes);
    }

    [Fact]
    public void SetCookTime_Allows_Null()
    {
        var recipe = NewRecipe();
        recipe.SetCookTime(30, Clock);

        recipe.SetCookTime(null, Clock);

        Assert.Null(recipe.CookTimeMinutes);
    }

    [Fact]
    public void SetDirections_Updates_Directions()
    {
        var recipe = NewRecipe();

        recipe.SetDirections("Boil water. Add pasta.", Clock);

        Assert.Equal("Boil water. Add pasta.", recipe.Directions);
    }

    // ── Tag management ────────────────────────────────────────────────────────

    [Fact]
    public void SetTags_Replaces_Tag_Set()
    {
        var recipe = NewRecipe();
        var tag1 = TagId.New();
        var tag2 = TagId.New();

        recipe.SetTags([tag1, tag2], Clock);

        Assert.Equal(2, recipe.Tags.Count);
        Assert.Contains(recipe.Tags, t => t.TagId == tag1);
        Assert.Contains(recipe.Tags, t => t.TagId == tag2);
        Assert.All(recipe.Tags, t => Assert.Equal(Household, t.HouseholdId));
    }

    [Fact]
    public void SetTags_Replaces_Previous_Tags_Wholesale()
    {
        var recipe = NewRecipe();
        recipe.SetTags([TagId.New(), TagId.New()], Clock);

        var newTag = TagId.New();
        recipe.SetTags([newTag], Clock);

        Assert.Single(recipe.Tags);
        Assert.Equal(newTag, recipe.Tags[0].TagId);
    }

    [Fact]
    public void SetTags_With_Empty_List_Clears_Tags()
    {
        var recipe = NewRecipe();
        recipe.SetTags([TagId.New()], Clock);

        recipe.SetTags([], Clock);

        Assert.Empty(recipe.Tags);
    }

    // ── Photo management ───────────────────────────────────────────────────────

    [Fact]
    public void SetPhoto_Creates_Photo_If_None_Exists()
    {
        var recipe = NewRecipe();
        var content = new byte[] { 1, 2, 3 };

        recipe.SetPhoto(content, "image/jpeg", null, Clock);

        Assert.NotNull(recipe.Photo);
        Assert.Equal(content, recipe.Photo!.Content);
        Assert.Equal("image/jpeg", recipe.Photo.ContentType);
        Assert.Null(recipe.Photo.Sha256);
        Assert.Equal(recipe.Id, recipe.Photo.Id);
    }

    [Fact]
    public void SetPhoto_Updates_Existing_Photo_In_Place()
    {
        var recipe = NewRecipe();
        recipe.SetPhoto([1, 2, 3], "image/jpeg", null, Clock);
        var originalPhoto = recipe.Photo;

        var newContent = new byte[] { 4, 5, 6 };
        recipe.SetPhoto(newContent, "image/png", [0xFF], Clock);

        // Same object updated in place
        Assert.Same(originalPhoto, recipe.Photo);
        Assert.Equal(newContent, recipe.Photo!.Content);
        Assert.Equal("image/png", recipe.Photo.ContentType);
        Assert.Equal(new byte[] { 0xFF }, recipe.Photo.Sha256);
    }

    [Fact]
    public void RemovePhoto_Clears_Photo()
    {
        var recipe = NewRecipe();
        recipe.SetPhoto([1, 2], "image/jpeg", null, Clock);

        recipe.RemovePhoto(Clock);

        Assert.Null(recipe.Photo);
    }

    [Fact]
    public void RemovePhoto_Is_NoOp_When_No_Photo()
    {
        var recipe = NewRecipe();

        // Should not throw
        recipe.RemovePhoto(Clock);

        Assert.Null(recipe.Photo);
    }

    // ── ReplaceIngredients ────────────────────────────────────────────────────

    [Fact]
    public void ReplaceIngredients_Succeeds_With_Valid_Lines()
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceIngredients(OneIngredient(), Clock);

        Assert.True(result.IsSuccess);
        Assert.Single(recipe.Ingredients);
    }

    [Fact]
    public void ReplaceIngredients_Sets_Household_And_RecipeId_On_Children()
    {
        var recipe = NewRecipe();

        recipe.ReplaceIngredients(OneIngredient(), Clock);

        Assert.All(recipe.Ingredients, i =>
        {
            Assert.Equal(Household, i.HouseholdId);
            Assert.Equal(recipe.Id, i.RecipeId);
        });
    }

    [Fact]
    public void ReplaceIngredients_Remints_IngredientIds_On_Each_Call()
    {
        var recipe = NewRecipe();
        recipe.ReplaceIngredients(OneIngredient(), Clock);
        var firstId = recipe.Ingredients[0].Id;

        recipe.ReplaceIngredients(OneIngredient(), Clock);
        var secondId = recipe.Ingredients[0].Id;

        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public void ReplaceIngredients_Emits_RecipeUpdated()
    {
        var recipe = NewRecipe();
        recipe.ClearDomainEvents();

        recipe.ReplaceIngredients(OneIngredient(), Clock);

        var evt = Assert.Single(recipe.DomainEvents);
        Assert.IsType<RecipeUpdatedEvent>(evt);
    }

    // R3 — at least one ingredient
    [Fact]
    public void ReplaceIngredients_R3_Rejects_Empty_List()
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceIngredients([], Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NoIngredients", result.Error.Code);
    }

    // R4 — product non-null / non-empty
    [Fact]
    public void ReplaceIngredients_R4_Rejects_Empty_ProductId()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.Empty, 1m, Guid.CreateVersion7(), null, 0) };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidProductId", result.Error.Code);
    }

    // R5 — qty/unit both-set or both-null
    [Fact]
    public void ReplaceIngredients_R5_Rejects_Qty_Without_Unit()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 500m, null, null, 0) };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.QtyUnitMismatch", result.Error.Code);
    }

    [Fact]
    public void ReplaceIngredients_R5_Rejects_Unit_Without_Qty()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), null, Guid.CreateVersion7(), null, 0) };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.QtyUnitMismatch", result.Error.Code);
    }

    [Fact]
    public void ReplaceIngredients_R5_Allows_Both_Null()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), null, null, null, 0) };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsSuccess);
        Assert.Null(recipe.Ingredients[0].Quantity);
        Assert.Null(recipe.Ingredients[0].UnitId);
    }

    // R6 — contiguous ordinals
    [Fact]
    public void ReplaceIngredients_R6_Rejects_NonContiguous_Ordinals()
    {
        var recipe = NewRecipe();
        var lines = new[]
        {
            new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0),
            new IngredientLine(Guid.CreateVersion7(), 2m, Guid.CreateVersion7(), null, 2), // gap!
        };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NonContiguousOrdinals", result.Error.Code);
    }

    [Fact]
    public void ReplaceIngredients_R6_Allows_Ordinals_Starting_From_One()
    {
        var recipe = NewRecipe();
        var lines = new[]
        {
            new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 1),
            new IngredientLine(Guid.CreateVersion7(), 2m, Guid.CreateVersion7(), null, 2),
        };

        var result = recipe.ReplaceIngredients(lines, Clock);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ReplaceIngredients_Replaces_Previous_List_Wholesale()
    {
        var recipe = NewRecipe();
        recipe.ReplaceIngredients(ThreeIngredients(), Clock);

        recipe.ReplaceIngredients(OneIngredient(), Clock);

        Assert.Single(recipe.Ingredients);
    }

    // ── ReplaceLines (ingredients + inclusions) ────────────────────────────────

    private static InclusionLine OneInclusion(RecipeId? subId = null, decimal servings = 2m, int ordinal = 0) =>
        new(subId ?? RecipeId.New(), servings, null, ordinal);

    [Fact]
    public void ReplaceLines_Accepts_Inclusions_Only_R3Prime()
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceLines([], [OneInclusion()], Clock);

        Assert.True(result.IsSuccess);
        Assert.Empty(recipe.Ingredients);
        Assert.Single(recipe.Inclusions);
    }

    [Fact]
    public void ReplaceLines_R3Prime_Rejects_Both_Empty()
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceLines([], [], Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NoIngredients", result.Error.Code);
    }

    [Fact]
    public void ReplaceLines_Accepts_Mixed_Ingredients_And_Inclusions()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0) };

        var result = recipe.ReplaceLines(lines, [OneInclusion(ordinal: 1)], Clock);

        Assert.True(result.IsSuccess);
        Assert.Single(recipe.Ingredients);
        Assert.Single(recipe.Inclusions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReplaceLines_N1_Rejects_NonPositive_Servings(int servings)
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceLines([], [OneInclusion(servings: servings)], Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidInclusionServings", result.Error.Code);
    }

    [Fact]
    public void ReplaceLines_N2_Rejects_Self_Inclusion()
    {
        var recipe = NewRecipe();

        var result = recipe.ReplaceLines([], [OneInclusion(subId: recipe.Id)], Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.SelfInclusion", result.Error.Code);
    }

    [Fact]
    public void ReplaceLines_N3_Rejects_NonContiguous_Ordinals_Across_Union()
    {
        var recipe = NewRecipe();
        // Ingredient at 0, inclusion at 2 — gap at 1 across the union.
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0) };

        var result = recipe.ReplaceLines(lines, [OneInclusion(ordinal: 2)], Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.NonContiguousOrdinals", result.Error.Code);
    }

    [Fact]
    public void ReplaceLines_N3_Accepts_Contiguous_Ordinals_Across_Union()
    {
        var recipe = NewRecipe();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0) };

        var result = recipe.ReplaceLines(lines, [OneInclusion(ordinal: 1)], Clock);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ReplaceLines_Sets_Household_RecipeId_SubRecipeId_On_Inclusions()
    {
        var recipe = NewRecipe();
        var sub = RecipeId.New();

        recipe.ReplaceLines([], [OneInclusion(subId: sub, servings: 3m)], Clock);

        var inc = Assert.Single(recipe.Inclusions);
        Assert.Equal(Household, inc.HouseholdId);
        Assert.Equal(recipe.Id, inc.RecipeId);
        Assert.Equal(sub, inc.SubRecipeId);
        Assert.Equal(3m, inc.Servings);
    }

    [Fact]
    public void ReplaceLines_Remints_InclusionIds_On_Each_Call()
    {
        var recipe = NewRecipe();
        recipe.ReplaceLines([], [OneInclusion()], Clock);
        var firstId = recipe.Inclusions[0].Id;

        recipe.ReplaceLines([], [OneInclusion()], Clock);

        Assert.NotEqual(firstId, recipe.Inclusions[0].Id);
    }

    [Fact]
    public void ReplaceLines_Emits_Single_RecipeUpdated()
    {
        var recipe = NewRecipe();
        recipe.ClearDomainEvents();
        var lines = new[] { new IngredientLine(Guid.CreateVersion7(), 1m, Guid.CreateVersion7(), null, 0) };

        recipe.ReplaceLines(lines, [OneInclusion(ordinal: 1)], Clock);

        var evt = Assert.Single(recipe.DomainEvents);
        Assert.IsType<RecipeUpdatedEvent>(evt);
    }

    [Fact]
    public void ReplaceIngredients_Overload_Clears_Existing_Inclusions()
    {
        var recipe = NewRecipe();
        recipe.ReplaceLines([], [OneInclusion()], Clock);
        Assert.Single(recipe.Inclusions);

        // The ingredient-only overload replaces BOTH line sets (inclusions = empty).
        recipe.ReplaceIngredients(OneIngredient(), Clock);

        Assert.Single(recipe.Ingredients);
        Assert.Empty(recipe.Inclusions);
    }

    // ── ChangeDefaultServings ─────────────────────────────────────────────────

    [Fact]
    public void ChangeDefaultServings_Keep_Updates_Servings_Only()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0)], Clock);
        var originalQty = recipe.Ingredients[0].Quantity;

        var result = recipe.ChangeDefaultServings(2, ScaleMode.Keep, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, recipe.DefaultServings);
        Assert.Equal(originalQty, recipe.Ingredients[0].Quantity);
    }

    [Fact]
    public void ChangeDefaultServings_Proportional_Scales_Ingredient_Quantities()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0)], Clock);

        var result = recipe.ChangeDefaultServings(2, ScaleMode.Proportional, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, recipe.DefaultServings);
        Assert.Equal(100m, recipe.Ingredients[0].Quantity); // 200 * (2/4)
    }

    [Fact]
    public void ChangeDefaultServings_Proportional_Scales_Inclusion_Servings()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceLines([], [OneInclusion(servings: 2m)], Clock);

        var result = recipe.ChangeDefaultServings(8, ScaleMode.Proportional, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(4m, recipe.Inclusions[0].Servings); // 2 * (8/4)
    }

    [Fact]
    public void ChangeDefaultServings_Keep_Leaves_Inclusion_Servings_Unchanged()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceLines([], [OneInclusion(servings: 2m)], Clock);

        var result = recipe.ChangeDefaultServings(8, ScaleMode.Keep, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(2m, recipe.Inclusions[0].Servings);
    }

    [Fact]
    public void ChangeDefaultServings_Proportional_Remints_InclusionIds()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceLines([], [OneInclusion(servings: 2m)], Clock);
        var originalId = recipe.Inclusions[0].Id;

        recipe.ChangeDefaultServings(2, ScaleMode.Proportional, Clock);

        Assert.NotEqual(originalId, recipe.Inclusions[0].Id);
    }

    [Fact]
    public void ChangeDefaultServings_Proportional_Leaves_Null_Qty_As_Null()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), null, null, null, 0)], Clock);

        var result = recipe.ChangeDefaultServings(8, ScaleMode.Proportional, Clock);

        Assert.True(result.IsSuccess);
        Assert.Null(recipe.Ingredients[0].Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChangeDefaultServings_Rejects_Servings_Below_One(int servings)
    {
        var recipe = NewRecipe();

        var result = recipe.ChangeDefaultServings(servings, ScaleMode.Keep, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidServings", result.Error.Code);
    }

    [Fact]
    public void ChangeDefaultServings_Proportional_Remints_IngredientIds()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0)], Clock);
        var originalId = recipe.Ingredients[0].Id;

        recipe.ChangeDefaultServings(2, ScaleMode.Proportional, Clock);

        Assert.NotEqual(originalId, recipe.Ingredients[0].Id);
    }

    [Fact]
    public void ChangeDefaultServings_Keep_Does_Not_Remint_IngredientIds()
    {
        var recipe = NewRecipe(servings: 4);
        recipe.ReplaceIngredients(
        [new IngredientLine(Guid.CreateVersion7(), 200m, Guid.CreateVersion7(), null, 0)], Clock);
        var originalId = recipe.Ingredients[0].Id;

        recipe.ChangeDefaultServings(2, ScaleMode.Keep, Clock);

        Assert.Equal(originalId, recipe.Ingredients[0].Id);
    }

    // ── Archive ───────────────────────────────────────────────────────────────

    [Fact]
    public void Archive_Stamps_ArchivedAt()
    {
        var recipe = NewRecipe();
        var later = new FixedClock(Later);

        recipe.Archive(later);

        Assert.Equal(Later, recipe.ArchivedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent_Keeping_Original_Timestamp()
    {
        var recipe = NewRecipe();
        recipe.Archive(new FixedClock(Later));
        var first = recipe.ArchivedAt;

        recipe.Archive(new FixedClock(Later.AddDays(1)));

        Assert.Equal(first, recipe.ArchivedAt);
    }

    // ── UpdatedAt touched ─────────────────────────────────────────────────────

    [Fact]
    public void All_Mutators_Touch_UpdatedAt()
    {
        var recipe = NewRecipe();
        var later = new FixedClock(Later);

        recipe.Rename("New Name", later);
        Assert.Equal(Later, recipe.UpdatedAt);

        recipe.SetSource("x", later);
        recipe.SetCookTime(10, later);
        recipe.SetDirections("do stuff", later);
        recipe.SetTags([], later);
        recipe.SetPhoto([1], "image/jpeg", null, later);
        recipe.RemovePhoto(later);
        recipe.ReplaceIngredients(OneIngredient(), later);
        recipe.ChangeDefaultServings(2, ScaleMode.Keep, later);

        Assert.Equal(Later, recipe.UpdatedAt);
    }

    // ── Diet-tag nudge reconciliation (plantry-qll2.3) ─────────────────────────

    [Fact]
    public void IngredientProductHash_Is_Order_Independent_Over_Distinct_ProductIds()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        // Same distinct set in different orders + a duplicate hash to the same value.
        Assert.Equal(
            Recipe.IngredientProductHash([a, b, c]),
            Recipe.IngredientProductHash([c, a, b, a]));

        // A different set hashes differently.
        Assert.NotEqual(
            Recipe.IngredientProductHash([a, b]),
            Recipe.IngredientProductHash([a, b, c]));

        // The empty set hashes to the empty string.
        Assert.Equal(string.Empty, Recipe.IngredientProductHash([]));
    }

    [Fact]
    public void DismissDietNudge_Stamps_The_Supplied_Expanded_Set_Hash()
    {
        var pasta = Guid.CreateVersion7();
        var cheese = Guid.CreateVersion7();
        var subOnlyProduct = Guid.CreateVersion7();
        var recipe = NewRecipe();
        recipe.ReplaceIngredients(
        [
            new IngredientLine(pasta, 200m, Guid.CreateVersion7(), null, 0),
            new IngredientLine(cheese, 50m, Guid.CreateVersion7(), null, 1),
        ], Clock);
        Assert.Null(recipe.DietNudgeDismissedHash);

        // D9: the app layer computes the EXPANDED-set hash (direct + every included sub's products) and passes
        // it in — the aggregate stamps exactly what it is given (it cannot read its own included sub-recipes).
        var expandedHash = Recipe.IngredientProductHash([pasta, cheese, subOnlyProduct]);
        recipe.DismissDietNudge(expandedHash, Clock);

        Assert.Equal(expandedHash, recipe.DietNudgeDismissedHash);
        // The stamped expanded hash is distinct from the aggregate's DIRECT-set hash when a sub adds a product.
        Assert.NotEqual(recipe.CurrentIngredientProductHash(), recipe.DietNudgeDismissedHash);
    }

    [Fact]
    public void RemoveTag_Drops_Only_The_Named_Tag()
    {
        var keep = TagId.New();
        var drop = TagId.New();
        var recipe = NewRecipe();
        recipe.SetTags([keep, drop], Clock);

        recipe.RemoveTag(drop, Clock);

        Assert.Equal([keep], recipe.Tags.Select(rt => rt.TagId));

        // Removing a tag that is not applied is a harmless no-op.
        recipe.RemoveTag(TagId.New(), Clock);
        Assert.Equal([keep], recipe.Tags.Select(rt => rt.TagId));
    }

    // ── Yield-on-cook (plantry-854a, recipe-composition.md §9) ────────────────

    [Fact]
    public void SetYield_Declares_A_Yield_When_All_Fields_Provided()
    {
        var recipe = NewRecipe();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        var result = recipe.SetYield(productId, 4m, unitId, Clock);

        Assert.True(result.IsSuccess);
        Assert.True(recipe.HasYield);
        Assert.Equal(productId, recipe.YieldProductId);
        Assert.Equal(4m, recipe.YieldQuantity);
        Assert.Equal(unitId, recipe.YieldUnitId);
    }

    [Fact]
    public void SetYield_With_All_Null_Clears_The_Yield()
    {
        var recipe = NewRecipe();
        recipe.SetYield(Guid.CreateVersion7(), 4m, Guid.CreateVersion7(), Clock);

        var result = recipe.SetYield(null, null, null, Clock);

        Assert.True(result.IsSuccess);
        Assert.False(recipe.HasYield);
        Assert.Null(recipe.YieldProductId);
        Assert.Null(recipe.YieldQuantity);
        Assert.Null(recipe.YieldUnitId);
    }

    [Fact]
    public void SetYield_Rejects_Missing_Product()
    {
        var recipe = NewRecipe();

        var result = recipe.SetYield(null, 4m, Guid.CreateVersion7(), Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidYieldProduct", result.Error.Code);
        Assert.False(recipe.HasYield);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void SetYield_Rejects_NonPositive_Quantity(int quantity)
    {
        var recipe = NewRecipe();

        var result = recipe.SetYield(Guid.CreateVersion7(), quantity, Guid.CreateVersion7(), Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidYieldQuantity", result.Error.Code);
    }

    [Fact]
    public void SetYield_Rejects_Missing_Unit()
    {
        var recipe = NewRecipe();

        var result = recipe.SetYield(Guid.CreateVersion7(), 4m, null, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidYieldUnit", result.Error.Code);
    }
}
