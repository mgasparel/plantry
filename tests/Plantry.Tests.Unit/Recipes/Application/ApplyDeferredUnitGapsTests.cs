using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 unit tests for <see cref="ApplyDeferredUnitGaps"/> (plantry-qll2.6) — the convergence step that
/// retro-applies <see cref="CookConsumeLineStatus.DeferredUnitGap"/> consume lines once a conversion
/// bridges the (product, unit-pair) they were waiting on.
/// <para>
/// Reuses the shared <c>FakeCookEventRepository</c>, <c>FakeInventoryConsumer</c>, and
/// <c>FakeTenantContext</c> test doubles from this namespace.
/// </para>
/// </summary>
public sealed class ApplyDeferredUnitGapsTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();
    private HouseholdId Household => HouseholdId.From(_householdGuid);

    private sealed class Harness
    {
        public required FakeCookEventRepository CookEvents { get; init; }
        public required FakeInventoryConsumer Consumer { get; init; }
        public required ApplyDeferredUnitGaps Service { get; init; }
    }

    private Harness BuildHarness(bool authenticated = true)
    {
        var cookEvents = new FakeCookEventRepository();
        var consumer = new FakeInventoryConsumer();
        var tenant = new FakeTenantContext(authenticated ? _householdGuid : null);
        return new Harness
        {
            CookEvents = cookEvents,
            Consumer = consumer,
            Service = new ApplyDeferredUnitGaps(cookEvents, consumer, tenant, NullLogger<ApplyDeferredUnitGaps>.Instance),
        };
    }

    private (CookEvent cookEvent, CookConsumeLine line) SeedDeferred(
        Harness h, Guid productId, Guid unitId, decimal quantity = 100m)
    {
        var cookEvent = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 2, Guid.CreateVersion7(), Clock).Value;
        var line = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, quantity, unitId);
        line.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(cookEvent);
        return (cookEvent, line);
    }

    // ── No-op paths ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoOp_When_No_Household()
    {
        var h = BuildHarness(authenticated: false);
        SeedDeferred(h, Guid.CreateVersion7(), Guid.CreateVersion7());

        var applied = await h.Service.ExecuteAsync([Guid.CreateVersion7()]);

        Assert.Equal(0, applied);
        Assert.Empty(h.Consumer.Calls);
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task NoOp_When_Empty_Product_Set()
    {
        var h = BuildHarness();
        SeedDeferred(h, Guid.CreateVersion7(), Guid.CreateVersion7());

        var applied = await h.Service.ExecuteAsync([]);

        Assert.Equal(0, applied);
        Assert.Empty(h.Consumer.Calls);
    }

    [Fact]
    public async Task NoOp_When_No_Deferred_Line_For_Requested_Product()
    {
        var h = BuildHarness();
        SeedDeferred(h, Guid.CreateVersion7(), Guid.CreateVersion7());

        // Ask for a DIFFERENT product than the one with the deferred line.
        var applied = await h.Service.ExecuteAsync([Guid.CreateVersion7()]);

        Assert.Equal(0, applied);
        Assert.Empty(h.Consumer.Calls);
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    // ── Retro-apply once a conversion lands ─────────────────────────────────────────

    [Fact]
    public async Task Applies_Deferred_Line_When_Consume_Now_Succeeds()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (cookEvent, line) = SeedDeferred(h, productId, unitId, quantity: 120m);

        var applied = await h.Service.ExecuteAsync([productId]);

        Assert.Equal(1, applied);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(0m, line.Shortfall);

        // The consume was re-driven with the original cook's identity + the line's own token.
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(productId, call.ProductId);
        Assert.Equal(120m, call.Quantity);
        Assert.Equal(unitId, call.UnitId);
        Assert.Equal(cookEvent.Id.Value, call.CookEventId);
        Assert.Equal(cookEvent.CookedBy, call.UserId);
        Assert.Equal(line.Id.Value, call.SourceLineRef);
        Assert.Equal(1, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Applies_With_Partial_Shortfall_When_Pantry_Undersatisfied()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (_, line) = SeedDeferred(h, productId, unitId, quantity: 100m);
        h.Consumer.SetShortfall(productId, 30m); // conversion bridges now, but only 70 on hand

        var applied = await h.Service.ExecuteAsync([productId]);

        Assert.Equal(1, applied);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(30m, line.Shortfall);
    }

    [Fact]
    public async Task Leaves_Line_Deferred_When_Conversion_Still_Does_Not_Bridge()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (_, line) = SeedDeferred(h, productId, unitId);
        // The conversion that landed did not bridge THIS pair.
        h.Consumer.ThrowUnitGap(productId);

        var applied = await h.Service.ExecuteAsync([productId]);

        Assert.Equal(0, applied);
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, line.Status);
        // No status change → nothing persisted for this event.
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Marks_Shorted_When_Stock_Record_Gone_Since_The_Cook()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (_, line) = SeedDeferred(h, productId, unitId, quantity: 90m);
        // The product's stock was fully removed since the cook — no stock record at all now.
        h.Consumer.ThrowNoStock(productId);

        var applied = await h.Service.ExecuteAsync([productId]);

        Assert.Equal(0, applied); // not counted as applied
        Assert.Equal(CookConsumeLineStatus.Shorted, line.Status);
        Assert.Equal(1, h.CookEvents.SaveChangesCalls); // the transition was persisted
    }

    // ── Only DeferredUnitGap lines for the requested product are touched ─────────────

    [Fact]
    public async Task Does_Not_Touch_Applied_Or_Shorted_Lines_On_The_Same_Product()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        var cookEvent = CookEvent.Record(RecipeId.New(), Household, 2, Guid.CreateVersion7(), Clock).Value;
        var appliedLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 10m, unitId);
        var shortedLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 20m, unitId);
        var deferredLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 30m, unitId);
        appliedLine.MarkApplied(0m);
        shortedLine.MarkShorted();
        deferredLine.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(cookEvent);

        var applied = await h.Service.ExecuteAsync([productId]);

        // Only the deferred line is re-driven.
        Assert.Equal(1, applied);
        Assert.Equal(CookConsumeLineStatus.Applied, deferredLine.Status);
        Assert.Equal(CookConsumeLineStatus.Applied, appliedLine.Status);
        Assert.Equal(CookConsumeLineStatus.Shorted, shortedLine.Status);
        var call = Assert.Single(h.Consumer.Calls);
        Assert.Equal(30m, call.Quantity);
    }

    [Fact]
    public async Task Applies_Across_Multiple_Cook_Events_For_The_Same_Product()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (_, lineA) = SeedDeferred(h, productId, unitId, quantity: 40m);
        var (_, lineB) = SeedDeferred(h, productId, unitId, quantity: 60m);

        var applied = await h.Service.ExecuteAsync([productId]);

        Assert.Equal(2, applied);
        Assert.Equal(CookConsumeLineStatus.Applied, lineA.Status);
        Assert.Equal(CookConsumeLineStatus.Applied, lineB.Status);
        Assert.Equal(2, h.Consumer.Calls.Count);
    }
}
