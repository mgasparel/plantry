using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 unit tests for <see cref="ReconcilePendingCooks"/> (plantry-292c).
/// <para>
/// Acceptance criteria tested:
/// <list type="bullet">
/// <item>Reconciliation re-drives ONLY Pending lines and transitions them to Applied/Shorted (L2).</item>
/// <item>Re-driving an already-applied line is a no-op (covered by 292a idempotency; observed here
/// as the consumer not being called for Applied/Shorted lines).</item>
/// <item>Returns zero when there are no Pending lines.</item>
/// <item>Returns the correct count of reconciled lines.</item>
/// <item>No-op when the tenant context has no household.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ReconcilePendingCooksTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();

    private sealed class Harness
    {
        public required FakeCookEventRepository CookEvents { get; init; }
        public required FakeInventoryConsumer Consumer { get; init; }
        public required ReconcilePendingCooks Service { get; init; }
        public HouseholdId Household { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var cookEvents = new FakeCookEventRepository();
        var consumer = new FakeInventoryConsumer();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : null);
        var service = new ReconcilePendingCooks(cookEvents, consumer, tenant);
        return new Harness
        {
            CookEvents = cookEvents,
            Consumer = consumer,
            Service = service,
            Household = HouseholdId.From(_householdGuid),
        };
    }

    /// <summary>
    /// Builds a CookEvent with one Pending consume line and registers it in the fake repository.
    /// Returns the cook event and the line's product id.
    /// </summary>
    private static (CookEvent cookEvent, Guid productId, Guid unitId) SeedPendingCook(
        Harness h, decimal quantity = 100m)
    {
        var recipeId = RecipeId.New();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var cookEvent = CookEvent.Record(recipeId, h.Household, servingsCooked: 2, userId, Clock).Value;
        cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, quantity, unitId);
        h.CookEvents.Items.Add(cookEvent);
        return (cookEvent, productId, unitId);
    }

    // ── No-op paths ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Zero_When_No_CookEvents_Have_Pending_Lines()
    {
        var h = BuildHarness();
        // No cook events at all.

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(0, result.ReconciledLineCount);
        Assert.False(result.HadWork);
        Assert.Empty(h.Consumer.Calls);
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Returns_Zero_When_All_Lines_Already_Applied()
    {
        var h = BuildHarness();
        var (cookEvent, productId, unitId) = SeedPendingCook(h);
        // Pre-transition the line to Applied so it does not appear Pending.
        var line = Assert.Single(cookEvent.ConsumeLines);
        line.MarkApplied(0m);

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(0, result.ReconciledLineCount);
        Assert.Empty(h.Consumer.Calls);
    }

    [Fact]
    public async Task Returns_Zero_When_All_Lines_Already_Shorted()
    {
        var h = BuildHarness();
        var (cookEvent, _, _) = SeedPendingCook(h);
        var line = Assert.Single(cookEvent.ConsumeLines);
        line.MarkShorted();

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(0, result.ReconciledLineCount);
        Assert.Empty(h.Consumer.Calls);
    }

    [Fact]
    public async Task Returns_Zero_When_Tenant_Has_No_Household()
    {
        var h = BuildHarness(authenticated: false);

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(0, result.ReconciledLineCount);
        Assert.Empty(h.Consumer.Calls);
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    // ── Pending lines are re-driven ────────────────────────────────────────────────

    [Fact]
    public async Task Reconciles_Single_Pending_Line_Via_ConsumeAsync()
    {
        var h = BuildHarness();
        var (cookEvent, productId, unitId) = SeedPendingCook(h, quantity: 200m);

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(1, result.ReconciledLineCount);
        Assert.True(result.HadWork);

        // Consumer was called exactly once for the pending line.
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(productId, call.ProductId);
        Assert.Equal(200m, call.Quantity);
        Assert.Equal(unitId, call.UnitId);
        Assert.Equal(ConsumeReason.Recipe, call.Reason);
        Assert.Equal(cookEvent.Id.Value, call.CookEventId);
        Assert.Equal(cookEvent.CookedBy, call.UserId);
    }

    [Fact]
    public async Task Reconciled_Line_Transitions_To_Applied_When_ConsumeAsync_Succeeds()
    {
        var h = BuildHarness();
        var (cookEvent, productId, _) = SeedPendingCook(h, quantity: 100m);
        h.Consumer.SetShortfall(productId, 0m);

        await h.Service.ExecuteAsync();

        var line = Assert.Single(cookEvent.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(0m, line.Shortfall);
    }

    [Fact]
    public async Task Reconciled_Line_Applied_With_Partial_Shortfall_When_Pantry_Undersatisfied()
    {
        var h = BuildHarness();
        var (cookEvent, productId, _) = SeedPendingCook(h, quantity: 100m);
        h.Consumer.SetShortfall(productId, 40m); // 60 available, 40 short

        await h.Service.ExecuteAsync();

        var line = Assert.Single(cookEvent.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(40m, line.Shortfall);
    }

    [Fact]
    public async Task Reconciled_Line_Transitions_To_Shorted_When_ConsumeAsync_Throws()
    {
        var h = BuildHarness();
        var (cookEvent, productId, _) = SeedPendingCook(h, quantity: 150m);
        h.Consumer.ThrowNoStock(productId);

        await h.Service.ExecuteAsync();

        var line = Assert.Single(cookEvent.ConsumeLines);
        Assert.Equal(CookConsumeLineStatus.Shorted, line.Status);
        Assert.Equal(150m, line.Shortfall); // MarkShorted sets Shortfall = Quantity
    }

    // ── Idempotency: only Pending lines are re-driven ──────────────────────────────

    [Fact]
    public async Task Only_Pending_Lines_Are_Re_Driven_Applied_And_Shorted_Are_Skipped()
    {
        var h = BuildHarness();
        var recipeId = RecipeId.New();
        var userId = Guid.CreateVersion7();
        var cookEvent = CookEvent.Record(recipeId, h.Household, servingsCooked: 2, userId, Clock).Value;

        var pendingProductId = Guid.CreateVersion7();
        var appliedProductId = Guid.CreateVersion7();
        var shortedProductId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        // Three lines: one Pending, one Applied, one Shorted.
        cookEvent.AddConsumeLine(Guid.CreateVersion7(), pendingProductId, 100m, unitId);
        var appliedLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), appliedProductId, 50m, unitId);
        var shortedLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), shortedProductId, 75m, unitId);
        appliedLine.MarkApplied(0m);
        shortedLine.MarkShorted();

        h.CookEvents.Items.Add(cookEvent);

        var result = await h.Service.ExecuteAsync();

        // Only the Pending line was reconciled.
        Assert.Equal(1, result.ReconciledLineCount);

        // Consumer called only for the pending line.
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(pendingProductId, call.ProductId);
    }

    // ── Multiple cook events with pending lines ─────────────────────────────────────

    [Fact]
    public async Task Reconciles_Pending_Lines_Across_Multiple_CookEvents()
    {
        var h = BuildHarness();
        var (_, productA, _) = SeedPendingCook(h, quantity: 100m);
        var (_, productB, _) = SeedPendingCook(h, quantity: 200m);

        var result = await h.Service.ExecuteAsync();

        Assert.Equal(2, result.ReconciledLineCount);
        Assert.Equal(2, h.Consumer.Calls.Count);
        Assert.Contains(h.Consumer.Calls, c => c.ProductId == productA);
        Assert.Contains(h.Consumer.Calls, c => c.ProductId == productB);

        // SaveChanges called once per CookEvent (two separate events).
        Assert.Equal(2, h.CookEvents.SaveChangesCalls);
    }

    // ── sourceLineRef is passed as the idempotency token ──────────────────────────

    [Fact]
    public async Task SourceLineRef_Is_The_IngredientId_Of_The_Pending_Line()
    {
        var h = BuildHarness();
        var recipeId = RecipeId.New();
        var userId = Guid.CreateVersion7();
        var cookEvent = CookEvent.Record(recipeId, h.Household, servingsCooked: 2, userId, Clock).Value;

        var ingredientId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        cookEvent.AddConsumeLine(ingredientId, productId, 100m, unitId);
        h.CookEvents.Items.Add(cookEvent);

        await h.Service.ExecuteAsync();

        var call = Assert.Single(h.Consumer.Calls);
        // The sourceLineRef must be the CookConsumeLine's IngredientId (292a idempotency token).
        Assert.Equal(ingredientId, call.SourceLineRef);
    }

    // ── SaveChanges is called per CookEvent ────────────────────────────────────────

    [Fact]
    public async Task SaveChanges_Called_Once_Per_CookEvent_With_Pending_Lines()
    {
        var h = BuildHarness();
        // Two distinct cook events, each with one pending line.
        SeedPendingCook(h);
        SeedPendingCook(h);

        await h.Service.ExecuteAsync();

        Assert.Equal(2, h.CookEvents.SaveChangesCalls);
    }
}
