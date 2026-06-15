using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 tests for the <see cref="CookRecipe"/> application service (P2-3c).
/// Uses fakes for IInventoryConsumer, ICatalogProductReader, ICookEventRepository, and IDomainEventDispatcher.
/// Does NOT re-test FEFO/lot-selection logic — that is covered by the Inventory Consume tests.
/// </summary>
public sealed class CookRecipeTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required FakeRecipeRepository Recipes { get; init; }
        public required FakeCookEventRepository CookEvents { get; init; }
        public required FakeInventoryConsumer Consumer { get; init; }
        public required FakeCatalogProductReader Products { get; init; }
        public required FakeDomainEventDispatcher EventDispatcher { get; init; }
        public required CookRecipe Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var recipes = new FakeRecipeRepository();
        var cookEvents = new FakeCookEventRepository();
        var consumer = new FakeInventoryConsumer();
        var products = new FakeCatalogProductReader();
        var dispatcher = new FakeDomainEventDispatcher();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : null);
        var service = new CookRecipe(recipes, cookEvents, consumer, products, dispatcher, Clock, tenant);
        return new Harness
        {
            Recipes = recipes,
            CookEvents = cookEvents,
            Consumer = consumer,
            Products = products,
            EventDispatcher = dispatcher,
            Service = service,
        };
    }

    private HouseholdId Household => HouseholdId.From(_householdGuid);

    /// <summary>
    /// Builds a minimal tracked recipe with one ingredient and registers the product in the catalog fake.
    /// Returns the recipe and the product id.
    /// </summary>
    private (Recipe recipe, Guid productId, Guid unitId) BuildTrackedRecipe(
        Harness h, int defaultServings = 4, decimal quantity = 200m)
    {
        var unitId = Guid.CreateVersion7();
        var product = h.Products.AddTracked(unitId);
        var recipe = BuildRecipe(Household, defaultServings, product.Id, quantity, unitId);
        h.Recipes.Items.Add(recipe);
        return (recipe, product.Id, unitId);
    }

    private static Recipe BuildRecipe(
        HouseholdId household, int defaultServings, Guid productId, decimal qty, Guid unitId)
    {
        var r = Recipe.Create(household, "Test Recipe", defaultServings, SystemClock.Instance).Value;
        r.ReplaceIngredients(
            [new IngredientLine(productId, qty, unitId, null, 0)],
            SystemClock.Instance);
        return r;
    }

    // ── Skip untracked staples (C12) ─────────────────────────────────────────────

    [Fact]
    public async Task Cook_Skips_Untracked_Staple_And_Does_Not_Consume()
    {
        var h = BuildHarness();
        // A staple has null Quantity/UnitId — untracked by definition (C12).
        var stapleProductId = Guid.CreateVersion7();
        var recipe = Recipe.Create(Household, "Soup", 2, Clock).Value;
        // Add an untracked staple ingredient (qty = null, unitId = null).
        recipe.ReplaceIngredients(
            [new IngredientLine(stapleProductId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 2, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        // No consumes for an untracked staple.
        Assert.Empty(h.Consumer.Calls);
        Assert.Empty(cooked.LineResults);
        // CookEvent is still written (anchor commit + status commit = 2 saves).
        Assert.Single(h.CookEvents.Items);
        Assert.Equal(2, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Cook_Skips_Product_Not_In_Catalog_During_Default_AutoSelection()
    {
        var h = BuildHarness();
        // Product has qty+unit but is NOT registered in the catalog reader → treated as untracked (C12).
        var unknownProductId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var recipe = Recipe.Create(Household, "Mystery", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(unknownProductId, 100m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 2, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<CookRecipeResult.Cooked>(result);
        Assert.Empty(h.Consumer.Calls);
    }

    // ── Consume-available with shortfall (C8/R9) ──────────────────────────────────

    [Fact]
    public async Task Cook_Consumes_Available_And_Records_Shortfall_Without_Blocking()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        // Pantry can only satisfy 150 of 200; consume-available, shortfall = 50.
        h.Consumer.SetShortfall(productId, 50m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        Assert.Equal(4, cooked.ServingsCooked);

        var line = Assert.Single(cooked.LineResults);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(200m, line.RequestedQuantity);
        Assert.True(line.HasShortfall);
        Assert.Equal(50m, line.ShortfallAmount);

        // CookEvent is persisted even when there is a shortfall.
        Assert.Single(h.CookEvents.Items);
        // RecipeCooked event is dispatched.
        Assert.Single(h.EventDispatcher.Dispatched);
        Assert.IsType<RecipeCookedEvent>(h.EventDispatcher.Dispatched[0]);
    }

    [Fact]
    public async Task Cook_Applies_ServingsScale_Before_Consuming()
    {
        var h = BuildHarness();
        // Recipe default = 4 servings, 200 per serving → cook for 8 = scale 2 → expect 400.
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 8, _userId, Resolutions: []);

        await h.Service.ExecuteAsync(command);

        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(400m, call.Quantity); // 200 * (8/4)
        Assert.Equal(unitId, call.UnitId);
    }

    // ── CookEvent written with correct servings/cookedBy ──────────────────────────

    [Fact]
    public async Task CookEvent_Written_With_Correct_ServingsCooked_And_CookedBy()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h, defaultServings: 4);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 6, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        Assert.Equal(6, cooked.ServingsCooked);

        var evt = Assert.Single(h.CookEvents.Items);
        Assert.Equal(6, evt.ServingsCooked);
        Assert.Equal(_userId, evt.CookedBy);
        Assert.Equal(HouseholdId.From(_householdGuid), evt.HouseholdId);
        Assert.Equal(recipe.Id, evt.RecipeId);
        Assert.Equal(cooked.CookEventId, evt.Id);
    }

    // ── RecipeCooked event (§9, O2) ──────────────────────────────────────────────

    [Fact]
    public async Task RecipeCookedEvent_Dispatched_With_Correct_Payload()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h, defaultServings: 2);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 2, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        var domainEvent = Assert.Single(h.EventDispatcher.Dispatched);
        var cookedEvent = Assert.IsType<RecipeCookedEvent>(domainEvent);

        Assert.Equal(recipe.Id, cookedEvent.RecipeId);
        Assert.Equal(HouseholdId.From(_householdGuid), cookedEvent.HouseholdId);
        Assert.Equal(2, cookedEvent.ServingsCooked);
        Assert.Equal(_userId, cookedEvent.CookedBy);
    }

    // ── Multi-variant split allocation (C7/C11) ────────────────────────────────────

    [Fact]
    public async Task Cook_Consumes_Each_Variant_Allocation_Independently()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var parentProductId = Guid.CreateVersion7();

        // Recipe has a parent product ingredient (200g).
        var recipe = Recipe.Create(Household, "Stew", 4, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(parentProductId, 200m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var ingredientId = recipe.Ingredients[0].Id;

        // User splits into two variant allocations (C11 variant disambiguation).
        // Both variants must be registered as tracked products so the service's C12 check passes.
        var variantA = h.Products.AddTracked(unitId, "VariantA").Id;
        var variantB = h.Products.AddTracked(unitId, "VariantB").Id;
        var resolution = new IngredientResolution(
            ingredientId,
            IsSkipped: false,
            Allocations:
            [
                new VariantAllocation(variantA, 120m, unitId),
                new VariantAllocation(variantB, 80m, unitId),
            ]);

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [resolution]);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        // Two consume calls — one per allocation.
        Assert.Equal(2, h.Consumer.Calls.Count);
        Assert.Contains(h.Consumer.Calls, c => c.ProductId == variantA && c.Quantity == 120m);
        Assert.Contains(h.Consumer.Calls, c => c.ProductId == variantB && c.Quantity == 80m);
        Assert.Equal(2, cooked.LineResults.Count);
    }

    // ── Explicit skip resolution (C9) ──────────────────────────────────────────────

    [Fact]
    public async Task Cook_Explicit_Skip_Drops_Ingredient_Without_Consuming()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h);

        var ingredientId = recipe.Ingredients[0].Id;
        var skipResolution = new IngredientResolution(ingredientId, IsSkipped: true, Allocations: []);

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [skipResolution]);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<CookRecipeResult.Cooked>(result);
        Assert.Empty(h.Consumer.Calls);
    }

    // ── CookEventId is the sourceRef on every consume call (atomicity note, §7) ────

    [Fact]
    public async Task CookEventId_Stamped_As_SourceRef_On_All_Consumes()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var pA = h.Products.AddTracked(unit, "A");
        var pB = h.Products.AddTracked(unit, "B");

        var recipe = Recipe.Create(Household, "Multi", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [
                new IngredientLine(pA.Id, 100m, unit, null, 0),
                new IngredientLine(pB.Id, 50m, unit, null, 1),
            ],
            Clock);
        h.Recipes.Items.Add(recipe);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 2, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        Assert.Equal(2, h.Consumer.Calls.Count);
        // Every consume must carry the CookEventId as its sourceRef.
        Assert.All(h.Consumer.Calls, c => Assert.Equal(cooked.CookEventId.Value, c.CookEventId));
    }

    // ── Atomicity: CookEvent persisted even when a consume has a shortfall ──────────

    [Fact]
    public async Task Atomicity_CookEvent_Written_When_All_Lines_Have_Shortfall()
    {
        var h = BuildHarness();
        var (recipe, productId, _) = BuildTrackedRecipe(h, quantity: 100m);

        // Simulate complete stock-out: shortfall equals the full requested quantity.
        h.Consumer.SetShortfall(productId, 100m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        // CookEvent is written even with a 100% shortfall (anchor commit + status commit = 2 saves).
        Assert.Single(h.CookEvents.Items);
        Assert.Equal(2, h.CookEvents.SaveChangesCalls);
        // The shortfall is reported but did not block.
        var line = Assert.Single(cooked.LineResults);
        Assert.Equal(100m, line.ShortfallAmount);
    }

    // ── Validation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Unauthorized_When_No_Household_In_Context()
    {
        var h = BuildHarness(authenticated: false);

        var command = new CookRecipeCommand(RecipeId.New(), DesiredServings: 2, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<CookRecipeResult.Invalid>(result);
        Assert.Equal("Unauthorized", invalid.Error.Code);
        Assert.Empty(h.CookEvents.Items);
    }

    [Fact]
    public async Task Returns_NotFound_When_Recipe_Does_Not_Exist()
    {
        var h = BuildHarness();

        var command = new CookRecipeCommand(RecipeId.New(), DesiredServings: 4, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<CookRecipeResult.Invalid>(result);
        Assert.Equal("NotFound", invalid.Error.Code);
        Assert.Empty(h.CookEvents.Items);
    }

    [Fact]
    public async Task Returns_Invalid_When_DesiredServings_Is_Zero()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 0, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        var invalid = Assert.IsType<CookRecipeResult.Invalid>(result);
        Assert.Equal("Recipes.InvalidServings", invalid.Error.Code);
        Assert.Empty(h.CookEvents.Items);
    }

    // ── No-stock product (IInventoryConsumer throws) treated as full shortfall (C8) ───

    [Fact]
    public async Task Cook_Treats_NoStock_Exception_As_Full_Shortfall_And_Writes_CookEvent()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, quantity: 150m);

        // Consumer throws when the product has no stock record at all (IInventoryConsumer contract).
        h.Consumer.ThrowNoStock(productId);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        // Cook must not propagate the exception — treat it as a full shortfall (C8/R9).
        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);
        // CookEvent is still written (anchor commit + status commit = 2 saves).
        Assert.Single(h.CookEvents.Items);
        Assert.Equal(2, h.CookEvents.SaveChangesCalls);
        // The line reports a full shortfall equal to the requested quantity.
        var line = Assert.Single(cooked.LineResults);
        Assert.Equal(150m, line.ShortfallAmount);
        Assert.Equal(unitId, line.ShortfallUnitId);
        Assert.True(line.HasShortfall);
    }

    // ── Anchor-first ordering: CookEvent+lines committed before any consume (292b L2) ───

    [Fact]
    public async Task AnchorFirst_CookEvent_And_Lines_Committed_Before_First_Consume()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, quantity: 100m);

        // Record the order of saves vs. consume calls.
        var saveCount = 0;
        var consumeCallsSeen = new List<int>(); // consume count at each save
        h.CookEvents.OnSaveChanges = () => saveCount++;
        h.Consumer.OnConsumeAsync = () => consumeCallsSeen.Add(saveCount);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        // First consume must have seen exactly 1 SaveChanges already (the anchor commit).
        Assert.NotEmpty(consumeCallsSeen);
        Assert.Equal(1, consumeCallsSeen[0]);
    }

    // ── After cook: all lines are Applied or Shorted, none left Pending (292b L2) ───

    [Fact]
    public async Task AfterCook_Applied_Line_Has_Applied_Status()
    {
        var h = BuildHarness();
        var (recipe, productId, _) = BuildTrackedRecipe(h, quantity: 200m);
        // No shortfall — fully satisfied.
        h.Consumer.SetShortfall(productId, 0m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        var evt = Assert.Single(h.CookEvents.Items);
        var line = Assert.Single(evt.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(0m, line.Shortfall);
    }

    [Fact]
    public async Task AfterCook_Partial_Shortfall_Line_Has_Applied_Status_With_Shortfall()
    {
        var h = BuildHarness();
        var (recipe, productId, _) = BuildTrackedRecipe(h, quantity: 200m);
        // Partial shortfall — pantry satisfied 150 of 200.
        h.Consumer.SetShortfall(productId, 50m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        var evt = Assert.Single(h.CookEvents.Items);
        var line = Assert.Single(evt.ConsumeLines);
        // A partial shortfall still produced a journal row — line is Applied (not Shorted).
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(50m, line.Shortfall);
    }

    [Fact]
    public async Task AfterCook_NoStock_Line_Has_Shorted_Status()
    {
        var h = BuildHarness();
        var (recipe, productId, _) = BuildTrackedRecipe(h, quantity: 150m);
        h.Consumer.ThrowNoStock(productId);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        var evt = Assert.Single(h.CookEvents.Items);
        var line = Assert.Single(evt.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Shorted, line.Status);
        Assert.Equal(150m, line.Shortfall);
    }

    [Fact]
    public async Task AfterCook_No_Lines_Remain_Pending()
    {
        var h = BuildHarness();
        var unit = Guid.CreateVersion7();
        var pA = h.Products.AddTracked(unit, "A");
        var pB = h.Products.AddTracked(unit, "B");

        var recipe = Recipe.Create(Household, "Mixed", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [
                new IngredientLine(pA.Id, 100m, unit, null, 0),
                new IngredientLine(pB.Id, 50m, unit, null, 1),
            ],
            Clock);
        h.Recipes.Items.Add(recipe);

        // A is fully satisfied; B has no stock.
        h.Consumer.SetShortfall(pA.Id, 0m);
        h.Consumer.ThrowNoStock(pB.Id);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 2, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        var evt = Assert.Single(h.CookEvents.Items);
        Assert.Equal(2, evt.ConsumeLines.Count);
        // No line should remain Pending after a completed cook.
        Assert.DoesNotContain(evt.ConsumeLines, l => l.Status == CookConsumeLineStatus.Pending);
    }

    [Fact]
    public async Task AnchorFirst_ConsumeLines_Present_And_Pending_At_First_Save()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, quantity: 100m);

        // Capture a snapshot of each line's status at the time of the first (anchor) save.
        // Must capture value types (Status is an enum) not object references, because
        // MarkApplied/MarkShorted mutate the line in-place after the first save.
        List<CookConsumeLineStatus>? statusesAtFirstSave = null;
        h.CookEvents.OnSaveChanges = () =>
        {
            if (statusesAtFirstSave is null && h.CookEvents.Items.Count > 0)
                statusesAtFirstSave = [.. h.CookEvents.Items[0].ConsumeLines.Select(l => l.Status)];
        };

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        await h.Service.ExecuteAsync(command);

        // Lines should have been present and Pending at the time of the first (anchor) save.
        Assert.NotNull(statusesAtFirstSave);
        Assert.Single(statusesAtFirstSave);
        Assert.Equal(CookConsumeLineStatus.Pending, statusesAtFirstSave[0]);
    }

    // ── Untracked variant in explicit allocation is skipped (C12) ────────────────────

    [Fact]
    public async Task Cook_Skips_Untracked_Variant_In_Explicit_Allocation()
    {
        var h = BuildHarness();
        var unitId = Guid.CreateVersion7();
        var parentProductId = Guid.CreateVersion7();

        var recipe = Recipe.Create(Household, "Dish", 2, Clock).Value;
        recipe.ReplaceIngredients(
            [new IngredientLine(parentProductId, 100m, unitId, null, 0)],
            Clock);
        h.Recipes.Items.Add(recipe);

        var ingredientId = recipe.Ingredients[0].Id;

        // Two allocations: one tracked variant, one untracked variant.
        var trackedVariant = h.Products.AddTracked(unitId, "TrackedVariant");
        var untrackedVariantId = Guid.CreateVersion7(); // NOT registered in catalog → untracked/missing
        // Register untracked product explicitly.
        h.Products.Register(new CatalogProduct(untrackedVariantId, "Untracked", TrackStock: false,
            unitId, parentProductId, false, []));

        var resolution = new IngredientResolution(
            ingredientId,
            IsSkipped: false,
            Allocations:
            [
                new VariantAllocation(trackedVariant.Id, 60m, unitId),
                new VariantAllocation(untrackedVariantId, 40m, unitId), // should be skipped (C12)
            ]);

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 2, _userId, Resolutions: [resolution]);

        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<CookRecipeResult.Cooked>(result);
        // Only the tracked variant should be consumed — untracked variant is skipped.
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(trackedVariant.Id, call.ProductId);
        Assert.Equal(60m, call.Quantity);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────────────────────────

internal sealed class FakeCookEventRepository : ICookEventRepository
{
    public List<CookEvent> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    /// <summary>
    /// Optional callback invoked at the start of each <see cref="SaveChangesAsync"/> call,
    /// before incrementing <see cref="SaveChangesCalls"/>. Used to observe state at save time
    /// in ordering tests (292b L2).
    /// </summary>
    public Action? OnSaveChanges { get; set; }

    public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default)
    {
        Items.Add(cookEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>(Items.Where(e => e.RecipeId == recipeId)
            .OrderByDescending(e => e.CookedAt).ToList());

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        OnSaveChanges?.Invoke();
        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeInventoryConsumer : IInventoryConsumer
{
    public sealed record ConsumeCall(
        Guid ProductId, decimal Quantity, Guid UnitId,
        ConsumeReason Reason, Guid CookEventId, Guid UserId, Guid SourceLineRef);

    public List<ConsumeCall> Calls { get; } = [];
    private readonly Dictionary<Guid, decimal> _shortfalls = [];
    private readonly HashSet<Guid> _noStockThrows = [];

    /// <summary>
    /// Optional callback invoked at the start of each <see cref="ConsumeAsync"/> call.
    /// Used in ordering tests (292b L2) to observe state before the consume executes.
    /// </summary>
    public Action? OnConsumeAsync { get; set; }

    public void SetShortfall(Guid productId, decimal shortfall) =>
        _shortfalls[productId] = shortfall;

    /// <summary>
    /// Configures the fake to throw <see cref="InvalidOperationException"/> for
    /// <paramref name="productId"/> — simulating the no-stock-record case (see
    /// IInventoryConsumer.cs contract: throws when product has no stock record at all).
    /// </summary>
    public void ThrowNoStock(Guid productId) =>
        _noStockThrows.Add(productId);

    public Task<ConsumeResult> ConsumeAsync(
        Guid productId, decimal quantity, Guid unitId,
        ConsumeReason reason, Guid cookEventId, Guid userId,
        Guid sourceLineRef, CancellationToken ct = default)
    {
        OnConsumeAsync?.Invoke();

        if (_noStockThrows.Contains(productId))
            throw new InvalidOperationException($"No stock record for product {productId}.");

        Calls.Add(new ConsumeCall(productId, quantity, unitId, reason, cookEventId, userId, sourceLineRef));
        var shortfall = _shortfalls.GetValueOrDefault(productId, 0m);
        return Task.FromResult(new ConsumeResult(shortfall, unitId));
    }
}

internal sealed class FakeDomainEventDispatcher : IDomainEventDispatcher
{
    public List<IDomainEvent> Dispatched { get; } = [];

    public Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default)
    {
        Dispatched.AddRange(events);
        return Task.CompletedTask;
    }
}
