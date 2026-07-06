using Microsoft.Extensions.Logging.Abstractions;
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
/// <remarks>
/// Serialised in "DomainMeterListenerTests": executing CookRecipe increments the global
/// plantry.recipes.cooked meter counter. DomainTelemetryTests subscribes a process-wide
/// MeterListener and asserts exact delta counts — running in parallel with this class would
/// cause a spurious +1 on that delta and a flaky assertion.
/// </remarks>
[Collection("DomainMeterListenerTests")]
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
        var reconciler = new ReconcilePendingCooks(cookEvents, consumer, tenant, NullLogger<ReconcilePendingCooks>.Instance);
        var deferredUnitGaps = new ApplyDeferredUnitGaps(cookEvents, consumer, tenant, NullLogger<ApplyDeferredUnitGaps>.Instance);
        var service = new CookRecipe(recipes, cookEvents, consumer, products, dispatcher, Clock, tenant, reconciler,
            deferredUnitGaps, NullLogger<CookRecipe>.Instance);
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

    // ── Unit gap → DeferredUnitGap, not Shorted (plantry-qll2.6) ─────────────────────

    [Fact]
    public async Task Cook_Unreachable_Conversion_Lands_As_DeferredUnitGap_Not_Shorted()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, quantity: 150m);
        // Product has stock but no conversion bridges the ingredient unit → DeferredUnitGapException.
        h.Consumer.ThrowUnitGap(productId);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        var result = await h.Service.ExecuteAsync(command);

        // Cook still completes (never blocks, never asks) and the CookEvent is written.
        Assert.IsType<CookRecipeResult.Cooked>(result);
        var evt = Assert.Single(h.CookEvents.Items);
        var line = Assert.Single(evt.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, line.Status);
        // The full requested quantity is owed; the pantry was untouched.
        Assert.Equal(150m, line.Shortfall);
    }

    // ── Opportunistic deferred self-heal at cook entry (plantry-qll2.6 acceptance #6) ───

    [Fact]
    public async Task Cook_SelfHeals_Prior_Deferred_Line_For_A_Product_In_This_Cooks_Set()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 100m);

        // Seed a PRIOR cook that deferred a consume for the SAME product (unit gap at the time).
        var priorCook = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 1, _userId, Clock).Value;
        var deferredLine = priorCook.AddConsumeLine(Guid.CreateVersion7(), productId, 80m, unitId);
        deferredLine.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(priorCook);

        // A conversion has since landed — the consumer now succeeds for this product.
        // (No unit-gap throw configured, so ConsumeAsync returns normally.)
        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<CookRecipeResult.Cooked>(result);
        // The prior deferred line was retro-applied by the opportunistic self-heal before the new cook ran.
        Assert.Equal(CookConsumeLineStatus.Applied, deferredLine.Status);
    }

    [Fact]
    public async Task Cook_SelfHeal_Leaves_Deferred_Line_When_Conversion_Still_Missing()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 100m);

        // Prior deferred line for the same product, and the conversion STILL has not landed.
        var priorCook = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 1, _userId, Clock).Value;
        var deferredLine = priorCook.AddConsumeLine(Guid.CreateVersion7(), productId, 80m, unitId);
        deferredLine.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(priorCook);

        // The unit gap persists — both the self-heal re-drive and the new cook's own consume defer.
        h.Consumer.ThrowUnitGap(productId);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);
        var result = await h.Service.ExecuteAsync(command);

        Assert.IsType<CookRecipeResult.Cooked>(result);
        // Still deferred — the self-heal did not fabricate an application without a conversion.
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, deferredLine.Status);
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

    // ── Ad-hoc added products (plantry-7zjm) ─────────────────────────────────────

    /// <summary>
    /// AC1/AC6: an added existing product is consumed as part of the cook (ConsumeReason.Recipe,
    /// sourceRef → this cook) at the entered quantity + unit, and its consume line carries the
    /// Guid.Empty ingredient sentinel — nothing resolves it against a recipe ingredient.
    /// </summary>
    [Fact]
    public async Task Cook_AddsAdHocProduct_ConsumesAsRecipe_WithEmptyIngredientSentinel()
    {
        var h = BuildHarness();
        var (recipe, recipeProductId, _) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        // An existing catalog product the user adds to just this cook.
        var addedUnit = Guid.CreateVersion7();
        var added = h.Products.AddTracked(addedUnit, "Added Product");

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [],
            AdHocLines: [new AdHocLine(added.Id, 3m, addedUnit)]);

        var result = await h.Service.ExecuteAsync(command);
        var cooked = Assert.IsType<CookRecipeResult.Cooked>(result);

        // The added product was consumed at the entered qty + unit, attributed to this cook.
        var addedCall = Assert.Single(h.Consumer.Calls, c => c.ProductId == added.Id);
        Assert.Equal(3m, addedCall.Quantity);
        Assert.Equal(addedUnit, addedCall.UnitId);
        Assert.Equal(ConsumeReason.Recipe, addedCall.Reason);
        Assert.Equal(cooked.CookEventId.Value, addedCall.CookEventId); // sourceRef → this cook

        // The recipe's own ingredient is still consumed — the add is additive, not a replacement.
        Assert.Contains(h.Consumer.Calls, c => c.ProductId == recipeProductId);

        // The added line carries the Guid.Empty ingredient sentinel and is Applied…
        var evt = Assert.Single(h.CookEvents.Items);
        var addedLine = Assert.Single(evt.ConsumeLines, l => l.ProductId == added.Id);
        Assert.Equal(Guid.Empty, addedLine.IngredientId);
        Assert.Equal(CookConsumeLineStatus.Applied, addedLine.Status);
        // …and its idempotency token is its OWN line id, NOT the empty ingredient id (plantry-fks).
        Assert.Equal(addedLine.Id.Value, addedCall.SourceLineRef);
        Assert.NotEqual(Guid.Empty, addedCall.SourceLineRef);
    }

    /// <summary>
    /// AC2: removing a recipe ingredient (skip) and adding a different product composes an on-the-fly
    /// substitution with no dedicated substitute mode — only the replacement is consumed.
    /// </summary>
    [Fact]
    public async Task Cook_SkipPlusAdd_ComposesSubstitution_WithoutDedicatedMode()
    {
        var h = BuildHarness();
        var (recipe, originalProductId, _) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);
        var ingredientId = recipe.Ingredients[0].Id;

        var replacementUnit = Guid.CreateVersion7();
        var replacement = h.Products.AddTracked(replacementUnit, "Replacement");

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId,
            Resolutions: [new IngredientResolution(ingredientId, IsSkipped: true, Allocations: [])],
            AdHocLines: [new AdHocLine(replacement.Id, 1m, replacementUnit)]);

        var result = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result);

        // The original recipe product was skipped — not consumed.
        Assert.DoesNotContain(h.Consumer.Calls, c => c.ProductId == originalProductId);
        // Only the replacement was consumed.
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(replacement.Id, call.ProductId);
        Assert.Equal(1m, call.Quantity);
        Assert.Equal(replacementUnit, call.UnitId);
    }

    /// <summary>
    /// AC3: an added product whose stock unit cannot convert to the entered unit defers
    /// (DeferredUnitGap) exactly like a recipe ingredient would — never Shorted — with the full
    /// requested quantity recorded as owed and the Guid.Empty ingredient sentinel intact.
    /// </summary>
    [Fact]
    public async Task Cook_AdHocProduct_UnitGap_DefersLikeIngredient()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        var addedUnit = Guid.CreateVersion7();
        var added = h.Products.AddTracked(addedUnit, "Added");
        h.Consumer.ThrowUnitGap(added.Id);

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [],
            AdHocLines: [new AdHocLine(added.Id, 5m, addedUnit)]);

        var result = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result);

        var evt = Assert.Single(h.CookEvents.Items);
        var addedLine = Assert.Single(evt.ConsumeLines, l => l.ProductId == added.Id);
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, addedLine.Status);
        Assert.Equal(Guid.Empty, addedLine.IngredientId);
        Assert.Equal(5m, addedLine.Shortfall); // full requested quantity owed
    }

    /// <summary>
    /// C12 consistency (interpretation, plantry-7zjm): an added product that is untracked has no stock
    /// to consume, so it is skipped exactly as an untracked recipe ingredient would be — no consume, no
    /// doomed consume line minted.
    /// </summary>
    [Fact]
    public async Task Cook_AdHocUntrackedProduct_IsSkipped()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        var addedUnit = Guid.CreateVersion7();
        var untrackedId = Guid.CreateVersion7();
        h.Products.RegisterUntracked(untrackedId, "Untracked Add");

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [],
            AdHocLines: [new AdHocLine(untrackedId, 2m, addedUnit)]);

        var result = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result);

        Assert.DoesNotContain(h.Consumer.Calls, c => c.ProductId == untrackedId);
        var evt = Assert.Single(h.CookEvents.Items);
        Assert.DoesNotContain(evt.ConsumeLines, l => l.ProductId == untrackedId);
    }

    /// <summary>
    /// A malformed added row (non-positive quantity or empty unit) never mints a consume line — the
    /// service guards it defensively even though the Web layer already filters such rows.
    /// </summary>
    [Fact]
    public async Task Cook_AdHocProduct_WithNonPositiveQuantity_IsSkipped()
    {
        var h = BuildHarness();
        var (recipe, _, _) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        var addedUnit = Guid.CreateVersion7();
        var added = h.Products.AddTracked(addedUnit, "Added");

        var command = new CookRecipeCommand(
            recipe.Id, DesiredServings: 4, _userId, Resolutions: [],
            AdHocLines: [new AdHocLine(added.Id, 0m, addedUnit)]);

        var result = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result);

        Assert.DoesNotContain(h.Consumer.Calls, c => c.ProductId == added.Id);
    }

    // ── Opportunistic reconciliation sweep (292c) ────────────────────────────────

    /// <summary>
    /// Verifies that ReconcilePendingCooks runs before the new cook starts: a Pending consume line
    /// from a prior interrupted cook is transitioned to Applied during the sweep, and the new cook
    /// still completes successfully.
    /// </summary>
    [Fact]
    public async Task Opportunistic_Sweep_Reconciles_Pending_Lines_Before_New_Cook()
    {
        var h = BuildHarness();

        // Seed a prior interrupted cook: one Pending consume line.
        var priorCook = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 1,
            Guid.CreateVersion7(), Clock).Value;
        var priorProductId = Guid.CreateVersion7();
        var priorUnitId = Guid.CreateVersion7();
        priorCook.AddConsumeLine(Guid.CreateVersion7(), priorProductId, 50m, priorUnitId);
        h.CookEvents.Items.Add(priorCook);

        // Build and start a new cook for a different recipe.
        var (recipe, _, _) = BuildTrackedRecipe(h);
        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        var result = await h.Service.ExecuteAsync(command);

        // New cook completed successfully.
        Assert.IsType<CookRecipeResult.Cooked>(result);

        // The prior Pending line was swept to Applied or Shorted during reconciliation.
        var priorLine = Assert.Single(priorCook.ConsumeLines);
        Assert.NotEqual(CookConsumeLineStatus.Pending, priorLine.Status);
    }

    /// <summary>
    /// Verifies that a non-cancellation failure in the reconciliation sweep is swallowed and
    /// the new cook still proceeds to a Cooked result (reconciliation is best-effort).
    /// </summary>
    [Fact]
    public async Task Cook_Proceeds_When_Reconciliation_Sweep_Fails()
    {
        var h = BuildHarness();

        // Wire the repository to throw a non-OCE on ListWithPendingLinesAsync so the
        // sweep fails before it can do any work.
        h.CookEvents.OnListWithPendingLines = _ =>
            throw new InvalidOperationException("Simulated reconciliation failure.");

        var (recipe, _, _) = BuildTrackedRecipe(h);
        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        // Should not throw — reconciliation failure is swallowed, cook proceeds.
        var result = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result);
    }

    // ── Idempotency scope: each cook uses its own per-line token (plantry-fks) ─────

    /// <summary>
    /// Regression test (plantry-fks): cooking the same recipe twice must produce two independent
    /// consume calls — one per cook — and must NOT short-circuit the second cook.
    /// Before the fix, sourceLineRef was set to line.IngredientId (stable across cooks), so the
    /// second cook's consume was treated as a duplicate of the first and silently skipped.
    /// After the fix, sourceLineRef is line.Id.Value (CookConsumeLineId, unique per cook).
    /// </summary>
    [Fact]
    public async Task CookingSameRecipeTwice_IssuesTowoIndependentConsumeCalls()
    {
        var h = BuildHarness();
        var (recipe, productId, unitId) = BuildTrackedRecipe(h, defaultServings: 4, quantity: 200m);

        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        // First cook.
        var result1 = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result1);

        // Second cook of the same recipe with the same servings.
        var result2 = await h.Service.ExecuteAsync(command);
        Assert.IsType<CookRecipeResult.Cooked>(result2);

        // Each cook must have issued its OWN consume call — two total, not one.
        // (The reconciler runs as an opportunistic sweep before the second cook, but the
        // first cook's lines are already Applied, so the reconciler is a no-op here.)
        var cookCalls = h.Consumer.Calls.Where(c => c.ProductId == productId).ToList();
        Assert.Equal(2, cookCalls.Count);

        // Both consume calls must carry the SAME cookEvent.Id as sourceRef, but DIFFERENT sourceLineRefs
        // (each cook mints a new CookConsumeLine with a fresh Id).
        var sourceRefs = cookCalls.Select(c => c.CookEventId).Distinct().ToList();
        Assert.Equal(2, sourceRefs.Count); // two different CookEvent ids

        var lineRefs = cookCalls.Select(c => c.SourceLineRef).Distinct().ToList();
        Assert.Equal(2, lineRefs.Count); // two different sourceLineRef tokens
    }

    /// <summary>
    /// Verifies that an OperationCanceledException from the reconciliation sweep propagates out
    /// of ExecuteAsync (a cancelled request should abort the whole cook, not silently continue).
    /// </summary>
    [Fact]
    public async Task Cook_Aborts_When_Request_Cancelled_During_Reconciliation()
    {
        var h = BuildHarness();

        // Wire the repository to throw OCE on ListWithPendingLinesAsync so cancellation
        // propagates before any other work begins.
        using var cts = new CancellationTokenSource();
        h.CookEvents.OnListWithPendingLines = _ => throw new OperationCanceledException(cts.Token);

        var (recipe, _, _) = BuildTrackedRecipe(h);
        var command = new CookRecipeCommand(recipe.Id, DesiredServings: 4, _userId, Resolutions: []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Service.ExecuteAsync(command));
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

    /// <summary>
    /// Optional factory invoked instead of the default implementation of
    /// <see cref="ListWithPendingLinesAsync"/>. Used in opportunistic-sweep tests (292c) to
    /// inject failures (throw an exception) or a cancelled token.
    /// When set, the real list logic is bypassed entirely.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<CookEvent>>>? OnListWithPendingLines { get; set; }

    public Task AddAsync(CookEvent cookEvent, CancellationToken ct = default)
    {
        Items.Add(cookEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CookEvent>>(Items.Where(e => e.RecipeId == recipeId)
            .OrderByDescending(e => e.CookedAt).ToList());

    public Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default)
    {
        if (OnListWithPendingLines is not null)
            return OnListWithPendingLines(ct);
        return Task.FromResult<IReadOnlyList<CookEvent>>(Items
            .Where(e => e.ConsumeLines.Any(l => l.Status == CookConsumeLineStatus.Pending))
            .ToList());
    }

    public Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var set = productIds as HashSet<Guid> ?? [.. productIds];
        return Task.FromResult<IReadOnlyList<CookEvent>>(Items
            .Where(e => e.ConsumeLines.Any(l =>
                l.Status == CookConsumeLineStatus.DeferredUnitGap && set.Contains(l.ProductId)))
            .ToList());
    }

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
    private readonly HashSet<Guid> _unitGapThrows = [];

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

    /// <summary>
    /// Configures the fake to throw <see cref="DeferredUnitGapException"/> for
    /// <paramref name="productId"/> — simulating the no-conversion unit-gap case (plantry-qll2.6):
    /// the product HAS stock but no ProductConversion bridges the ingredient unit to the stock unit.
    /// </summary>
    public void ThrowUnitGap(Guid productId) =>
        _unitGapThrows.Add(productId);

    /// <summary>
    /// Simulates a conversion landing for <paramref name="productId"/>: subsequent consumes no longer
    /// throw the unit gap and instead succeed (with any configured shortfall).
    /// </summary>
    public void ResolveUnitGap(Guid productId) =>
        _unitGapThrows.Remove(productId);

    public Task<ConsumeResult> ConsumeAsync(
        Guid productId, decimal quantity, Guid unitId,
        ConsumeReason reason, Guid cookEventId, Guid userId,
        Guid sourceLineRef, CancellationToken ct = default)
    {
        OnConsumeAsync?.Invoke();

        if (_noStockThrows.Contains(productId))
            throw new InvalidOperationException($"No stock record for product {productId}.");

        if (_unitGapThrows.Contains(productId))
            throw new DeferredUnitGapException($"No conversion bridges the unit gap for product {productId}.");

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
