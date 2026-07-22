using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 tests for <see cref="RecipesUsingProductQuery"/> (plantry-o0r8) — the product→recipes cross-context
/// read backing the Pantry product Detail page's "Recipes" section. Covers the consumer match (ingredient
/// line), the producer match (declared yield, recipe-composition.md §9), a recipe with neither (excluded),
/// and household isolation.
/// </summary>
public sealed class RecipesUsingProductQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly HouseholdId OtherHousehold = HouseholdId.New();

    private static Recipe MakeConsumer(HouseholdId household, string name, Guid productId, Guid unitId)
    {
        var recipe = Recipe.Create(household, name, defaultServings: 2, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, 1m, unitId, null, 0)], Clock);
        return recipe;
    }

    private static Recipe MakeProducer(HouseholdId household, string name, Guid productId, Guid unitId)
    {
        var recipe = Recipe.Create(household, name, defaultServings: 2, Clock).Value;
        recipe.SetYield(productId, 4m, unitId, Clock);
        return recipe;
    }

    [Fact(DisplayName = "ExecuteAsync — a recipe with a matching ingredient line is a consumer match")]
    public async Task ConsumerMatch_IsReturned_WithIsConsumerTrue()
    {
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var repo = new FakeRecipeRepository(Household);
        var recipe = MakeConsumer(Household, "Chili", productId, unitId);
        repo.Items.Add(recipe);

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(Household.Value));
        var result = await query.ExecuteAsync(productId);

        var usage = Assert.Single(result);
        Assert.Equal(recipe.Id.Value, usage.RecipeId);
        Assert.Equal("Chili", usage.Name);
        Assert.True(usage.IsConsumer);
        Assert.False(usage.IsProducer);
    }

    [Fact(DisplayName = "ExecuteAsync — a recipe whose declared yield targets the product is a producer match")]
    public async Task ProducerMatch_IsReturned_WithIsProducerTrue()
    {
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var repo = new FakeRecipeRepository(Household);
        var recipe = MakeProducer(Household, "Stock", productId, unitId);
        repo.Items.Add(recipe);

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(Household.Value));
        var result = await query.ExecuteAsync(productId);

        var usage = Assert.Single(result);
        Assert.Equal(recipe.Id.Value, usage.RecipeId);
        Assert.Equal("Stock", usage.Name);
        Assert.False(usage.IsConsumer);
        Assert.True(usage.IsProducer);
    }

    [Fact(DisplayName = "ExecuteAsync — a recipe that neither consumes nor produces the product is excluded")]
    public async Task NoMatch_IsExcluded()
    {
        var productId = Guid.CreateVersion7();
        var unrelatedProductId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var repo = new FakeRecipeRepository(Household);
        repo.Items.Add(MakeConsumer(Household, "Unrelated soup", unrelatedProductId, unitId));

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(Household.Value));
        var result = await query.ExecuteAsync(productId);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ExecuteAsync — a matching recipe in another household is excluded (RLS-scoped repo)")]
    public async Task OtherHouseholdMatch_IsExcluded()
    {
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        // Repository is scoped to Household — mirrors the real repository's RLS query filter (ADR-008).
        // A matching recipe that belongs to a different household must never appear in the result even
        // though it references the same product.
        var repo = new FakeRecipeRepository(Household);
        repo.Items.Add(MakeConsumer(OtherHousehold, "Other household's chili", productId, unitId));

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(Household.Value));
        var result = await query.ExecuteAsync(productId);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ExecuteAsync — a recipe that is both a consumer and a producer sets both flags on one row")]
    public async Task BothConsumerAndProducer_SetsBothFlags_OnOneRow()
    {
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var repo = new FakeRecipeRepository(Household);
        var recipe = Recipe.Create(Household, "Reduction", defaultServings: 2, Clock).Value;
        recipe.ReplaceIngredients([new IngredientLine(productId, 1m, unitId, null, 0)], Clock);
        recipe.SetYield(productId, 2m, unitId, Clock);
        repo.Items.Add(recipe);

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(Household.Value));
        var result = await query.ExecuteAsync(productId);

        var usage = Assert.Single(result);
        Assert.True(usage.IsConsumer);
        Assert.True(usage.IsProducer);
    }

    [Fact(DisplayName = "ExecuteAsync — no household on the tenant context returns empty rather than throwing")]
    public async Task NoHousehold_ReturnsEmpty()
    {
        var productId = Guid.CreateVersion7();
        var repo = new FakeRecipeRepository(Household);

        var query = new RecipesUsingProductQuery(repo, new FakeTenantContext(householdId: null));
        var result = await query.ExecuteAsync(productId);

        Assert.Empty(result);
    }

    // ── Dedicated test double ───────────────────────────────────────────────────

    /// <summary>
    /// Household-scoped <see cref="IRecipeRepository"/> fake — unlike the shared <c>FakeRecipeRepository</c>
    /// in TestDoubles.cs (which does not filter by household, since its other consumers only ever seed a
    /// single household's data), this fake filters <see cref="ListForBrowseAsync"/> by
    /// <paramref name="household"/> to mirror the production repository's RLS query filter (ADR-008), so
    /// the "other household excluded" case actually exercises household scoping rather than trivially
    /// passing. <see cref="IRecipeRepository.ListRecipesReferencingProductAsync"/> is not overridden here —
    /// it uses the interface's default implementation, which delegates to <see cref="ListForBrowseAsync"/>,
    /// so this fake also verifies that default implementation end-to-end.
    /// </summary>
    private sealed class FakeRecipeRepository(HouseholdId household) : IRecipeRepository
    {
        public List<Recipe> Items { get; } = [];

        public Task AddAsync(Recipe recipe, CancellationToken ct = default)
        {
            Items.Add(recipe);
            return Task.CompletedTask;
        }

        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(r => r.Id == id && r.HouseholdId == household));

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>(
                Items.Where(r => r.HouseholdId == household && r.ArchivedAt == null).ToList());

        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(Items.Any(r => r.HouseholdId == householdId && r.ArchivedAt == null));

        public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
            IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
        {
            var wanted = ids.ToHashSet();
            IReadOnlyDictionary<RecipeId, string> result = Items
                .Where(r => wanted.Contains(r.Id) && r.HouseholdId == household && r.ArchivedAt == null)
                .ToDictionary(r => r.Id, r => r.Name);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);

        public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
            RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    }
}
