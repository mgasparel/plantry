using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L2 unit tests for <see cref="VoidDeferredUnitGapLines"/> (plantry-qll2.6) — the load-bearing safety
/// rule: an absolute observation (Take Stock count) on a product voids its pending
/// <see cref="CookConsumeLineStatus.DeferredUnitGap"/> lines so a later conversion cannot double-count
/// by retro-applying a delta the count already reflects.
/// </summary>
public sealed class VoidDeferredUnitGapLinesTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdGuid = Guid.NewGuid();
    private HouseholdId Household => HouseholdId.From(_householdGuid);

    private sealed class Harness
    {
        public required FakeCookEventRepository CookEvents { get; init; }
        public required VoidDeferredUnitGapLines VoidService { get; init; }
        public required ApplyDeferredUnitGaps ApplyService { get; init; }
        public required FakeInventoryConsumer Consumer { get; init; }
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
            VoidService = new VoidDeferredUnitGapLines(cookEvents, tenant, NullLogger<VoidDeferredUnitGapLines>.Instance),
            ApplyService = new ApplyDeferredUnitGaps(cookEvents, consumer, tenant, NullLogger<ApplyDeferredUnitGaps>.Instance),
        };
    }

    private (CookEvent cookEvent, CookConsumeLine line) SeedDeferred(Harness h, Guid productId, Guid unitId, decimal quantity = 100m)
    {
        var cookEvent = CookEvent.Record(RecipeId.New(), Household, 2, Guid.CreateVersion7(), Clock).Value;
        var line = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, quantity, unitId);
        line.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(cookEvent);
        return (cookEvent, line);
    }

    [Fact]
    public async Task NoOp_When_No_Household()
    {
        var h = BuildHarness(authenticated: false);
        var productId = Guid.CreateVersion7();
        SeedDeferred(h, productId, Guid.CreateVersion7());

        var voided = await h.VoidService.ExecuteAsync(productId);

        Assert.Equal(0, voided);
        Assert.Equal(0, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Voids_Deferred_Line_To_SupersededByCount()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var (_, line) = SeedDeferred(h, productId, Guid.CreateVersion7());

        var voided = await h.VoidService.ExecuteAsync(productId);

        Assert.Equal(1, voided);
        Assert.Equal(CookConsumeLineStatus.SupersededByCount, line.Status);
        Assert.Equal(0m, line.Shortfall);
        Assert.Equal(1, h.CookEvents.SaveChangesCalls);
    }

    [Fact]
    public async Task Does_Not_Touch_Applied_Or_Shorted_Or_Other_Products()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var otherProductId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();

        var cookEvent = CookEvent.Record(RecipeId.New(), Household, 2, Guid.CreateVersion7(), Clock).Value;
        var appliedLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 10m, unitId);
        var otherDeferred = cookEvent.AddConsumeLine(Guid.CreateVersion7(), otherProductId, 20m, unitId);
        var deferredLine = cookEvent.AddConsumeLine(Guid.CreateVersion7(), productId, 30m, unitId);
        appliedLine.MarkApplied(0m);
        otherDeferred.MarkDeferredUnitGap();
        deferredLine.MarkDeferredUnitGap();
        h.CookEvents.Items.Add(cookEvent);

        var voided = await h.VoidService.ExecuteAsync(productId);

        Assert.Equal(1, voided);
        Assert.Equal(CookConsumeLineStatus.SupersededByCount, deferredLine.Status);
        Assert.Equal(CookConsumeLineStatus.Applied, appliedLine.Status);
        // A deferred line for a DIFFERENT product is left intact.
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, otherDeferred.Status);
    }

    // ── The safety rule end-to-end: count voids, then a later conversion applies nothing ───

    [Fact]
    public async Task Conversion_Landing_After_A_Count_Applies_Nothing()
    {
        var h = BuildHarness();
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        var (_, line) = SeedDeferred(h, productId, unitId, quantity: 75m);

        // 1) An absolute count is recorded → the deferred line is voided.
        await h.VoidService.ExecuteAsync(productId);
        Assert.Equal(CookConsumeLineStatus.SupersededByCount, line.Status);

        // 2) The conversion the line was waiting on lands afterwards → apply must be a no-op.
        var applied = await h.ApplyService.ExecuteAsync([productId]);

        Assert.Equal(0, applied);
        Assert.Empty(h.Consumer.Calls); // no consume was re-driven — the count already captured reality
        Assert.Equal(CookConsumeLineStatus.SupersededByCount, line.Status);
    }
}
