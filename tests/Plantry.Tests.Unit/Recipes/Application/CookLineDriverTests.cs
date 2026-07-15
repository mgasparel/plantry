using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L1 direct unit tests for <see cref="CookLineDriver"/> (plantry-dq16) — the single owner of the cook
/// line-drive exception-to-status mapping shared by <see cref="CookRecipe"/> and
/// <see cref="ReconcilePendingCooks"/>. These tests pin the mapping itself (Applied / DeferredUnitGap /
/// Shorted for consumes; Applied / Failed for produces, plus <see cref="OperationCanceledException"/>
/// propagation) so it can never silently diverge between the two call sites. No mocks beyond the port
/// fakes; no orchestrator, no repository, no SaveChanges — the driver neither logs nor persists.
/// </summary>
public sealed class CookLineDriverTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.From(Guid.NewGuid());

    private static CookConsumeLine BuildConsumeLine(Guid productId, decimal quantity, Guid unitId)
    {
        var cookEvent = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 2, Guid.NewGuid(), Clock).Value;
        return cookEvent.AddConsumeLine(Guid.NewGuid(), productId, quantity, unitId);
    }

    private static CookProduceLine BuildProduceLine(Guid productId, decimal quantity, Guid unitId, DateOnly? expiry = null)
    {
        var cookEvent = CookEvent.Record(RecipeId.New(), Household, servingsCooked: 2, Guid.NewGuid(), Clock).Value;
        return cookEvent.AddProduceLine(productId, quantity, unitId, expiry);
    }

    // ── DriveConsumeAsync — Applied ──────────────────────────────────────────────

    [Fact]
    public async Task DriveConsume_On_Success_Marks_Applied_And_Reports_Result_Shortfall()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var consumer = new FakeInventoryConsumer();
        consumer.SetShortfall(productId, 30m); // partial: distinct from the line's full 100 quantity
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(productId, quantity: 100m, unitId);

        var outcome = await driver.DriveConsumeAsync(line, cookEventId: Guid.NewGuid(), userId: Guid.NewGuid());

        Assert.Equal(CookConsumeDriveStatus.Applied, outcome.Status);
        Assert.Equal(30m, outcome.ShortfallAmount); // the RESULT's shortfall, not line.Quantity
        Assert.Equal(unitId, outcome.ShortfallUnitId);
        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(30m, line.Shortfall);
    }

    [Fact]
    public async Task DriveConsume_Passes_Line_Identity_And_Idempotency_Token_To_Consumer()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cookEventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var consumer = new FakeInventoryConsumer();
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(productId, quantity: 100m, unitId);

        await driver.DriveConsumeAsync(line, cookEventId, userId);

        var call = Assert.Single(consumer.Calls);
        Assert.Equal(productId, call.ProductId);
        Assert.Equal(100m, call.Quantity);
        Assert.Equal(unitId, call.UnitId);
        Assert.Equal(ConsumeReason.Recipe, call.Reason);
        Assert.Equal(cookEventId, call.CookEventId);
        Assert.Equal(userId, call.UserId);
        Assert.Equal(line.Id.Value, call.SourceLineRef); // 292a idempotency token is the line's own id
    }

    // ── DriveConsumeAsync — DeferredUnitGap (caught BEFORE the no-stock catch) ────

    [Fact]
    public async Task DriveConsume_On_UnitGap_Marks_DeferredUnitGap_With_Full_Quantity_Owed()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var consumer = new FakeInventoryConsumer();
        consumer.ThrowUnitGap(productId);
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(productId, quantity: 100m, unitId);

        var outcome = await driver.DriveConsumeAsync(line, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(CookConsumeDriveStatus.DeferredUnitGap, outcome.Status);
        Assert.Equal(100m, outcome.ShortfallAmount); // full requested quantity is owed — pantry untouched
        Assert.Equal(unitId, outcome.ShortfallUnitId);
        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, line.Status);
        Assert.Equal(100m, line.Shortfall);
    }

    // ── DriveConsumeAsync — Shorted ──────────────────────────────────────────────

    [Fact]
    public async Task DriveConsume_On_NoStock_Marks_Shorted_With_Full_Quantity()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var consumer = new FakeInventoryConsumer();
        consumer.ThrowNoStock(productId);
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(productId, quantity: 100m, unitId);

        var outcome = await driver.DriveConsumeAsync(line, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(CookConsumeDriveStatus.Shorted, outcome.Status);
        Assert.Equal(100m, outcome.ShortfallAmount);
        Assert.Equal(unitId, outcome.ShortfallUnitId);
        Assert.Equal(CookConsumeLineStatus.Shorted, line.Status);
        Assert.Equal(100m, line.Shortfall);
    }

    // ── DriveConsumeAsync — cancellation propagates, line untouched ──────────────

    [Fact]
    public async Task DriveConsume_Propagates_OperationCanceled_And_Leaves_Line_Pending()
    {
        var consumer = new CancellingConsumer();
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(Guid.NewGuid(), quantity: 100m, Guid.NewGuid());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => driver.DriveConsumeAsync(line, Guid.NewGuid(), Guid.NewGuid()));

        // A cancelled request must NOT be recorded as a resolved (Shorted/Deferred/Applied) line.
        Assert.Equal(CookConsumeLineStatus.Pending, line.Status);
    }

    [Fact]
    public async Task DriveConsume_Propagates_TaskCanceled_As_OperationCanceled()
    {
        // TaskCanceledException derives from OperationCanceledException — must also propagate, not be
        // caught by the InvalidOperationException branch.
        var consumer = new CancellingConsumer(useTaskCanceled: true);
        var driver = new CookLineDriver(consumer, new FakeInventoryProducer());
        var line = BuildConsumeLine(Guid.NewGuid(), quantity: 100m, Guid.NewGuid());

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => driver.DriveConsumeAsync(line, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(CookConsumeLineStatus.Pending, line.Status);
    }

    // ── DriveProduceAsync — Applied ──────────────────────────────────────────────

    [Fact]
    public async Task DriveProduce_On_Success_Marks_Applied_With_No_Failure()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cookEventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var expiry = new DateOnly(2026, 8, 1);
        var producer = new FakeInventoryProducer();
        var driver = new CookLineDriver(new FakeInventoryConsumer(), producer);
        var line = BuildProduceLine(productId, quantity: 4m, unitId, expiry);

        var outcome = await driver.DriveProduceAsync(line, cookEventId, userId);

        Assert.Equal(CookProduceDriveStatus.Applied, outcome.Status);
        Assert.Null(outcome.Failure);
        Assert.Equal(CookProduceLineStatus.Applied, line.Status);

        var call = Assert.Single(producer.Calls);
        Assert.Equal(productId, call.ProductId);
        Assert.Equal(4m, call.Quantity);
        Assert.Equal(unitId, call.UnitId);
        Assert.Equal(expiry, call.ExpiryDate);
        Assert.Equal(ProduceReason.Recipe, call.Reason);
        Assert.Equal(cookEventId, call.CookEventId);
        Assert.Equal(userId, call.UserId);
        Assert.Equal(line.Id.Value, call.SourceLineRef);
    }

    // ── DriveProduceAsync — Failed carries the exception for the caller to log ────

    [Fact]
    public async Task DriveProduce_On_Failure_Marks_Failed_And_Returns_Caught_Exception()
    {
        var productId = Guid.NewGuid();
        var producer = new FakeInventoryProducer();
        producer.ThrowFail(productId);
        var driver = new CookLineDriver(new FakeInventoryConsumer(), producer);
        var line = BuildProduceLine(productId, quantity: 4m, Guid.NewGuid());

        var outcome = await driver.DriveProduceAsync(line, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(CookProduceDriveStatus.Failed, outcome.Status);
        Assert.IsType<InvalidOperationException>(outcome.Failure); // handed back so the caller can log it
        Assert.Equal(CookProduceLineStatus.Failed, line.Status);
    }

    // ── DriveProduceAsync — cancellation propagates, line untouched ──────────────

    [Fact]
    public async Task DriveProduce_Propagates_OperationCanceled_And_Leaves_Line_Pending()
    {
        var producer = new CancellingProducer();
        var driver = new CookLineDriver(new FakeInventoryConsumer(), producer);
        var line = BuildProduceLine(Guid.NewGuid(), quantity: 4m, Guid.NewGuid());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => driver.DriveProduceAsync(line, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(CookProduceLineStatus.Pending, line.Status);
    }

    // ── Cancellation-throwing fakes ──────────────────────────────────────────────

    private sealed class CancellingConsumer(bool useTaskCanceled = false) : IInventoryConsumer
    {
        public Task<ConsumeResult> ConsumeAsync(
            Guid productId, decimal quantity, Guid unitId,
            ConsumeReason reason, Guid cookEventId, Guid userId,
            Guid sourceLineRef, CancellationToken ct = default) =>
            useTaskCanceled
                ? throw new TaskCanceledException()
                : throw new OperationCanceledException();
    }

    private sealed class CancellingProducer : IInventoryProducer
    {
        public Task ProduceAsync(
            Guid productId, decimal quantity, Guid unitId, DateOnly? expiryDate,
            ProduceReason reason, Guid cookEventId, Guid userId,
            Guid sourceLineRef, CancellationToken ct = default) =>
            throw new OperationCanceledException();
    }
}
