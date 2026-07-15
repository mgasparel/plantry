using Plantry.Recipes.Domain;

namespace Plantry.Recipes.Application;

/// <summary>
/// The single owner of the cook line-drive protocol (plantry-dq16): the anchor-first crash-recovery
/// exception-to-status mapping that BOTH a live cook (<see cref="CookRecipe"/>) and crash-recovery
/// reconciliation (<see cref="ReconcilePendingCooks"/>) must apply identically for the same failure.
/// <para>
/// This is the correctness core of cooking — idempotent re-drive (plantry-292a/b/c), deferred unit gaps
/// (plantry-qll2.6), and yield-on-cook (plantry-854a). The mapping used to be duplicated verbatim in both
/// call sites, so every change (e.g. introducing <see cref="DeferredUnitGapException"/>) had to be
/// hand-edited in lockstep or a live cook and a reconcile would classify the same failure differently and
/// corrupt the self-healing guarantee. Centralising it here makes divergence impossible: there is exactly
/// one place the exception-to-status discrimination lives.
/// </para>
/// <para>
/// The driver owns ONLY the port call, the exception-to-status discrimination, and the resulting
/// <c>Mark*</c> transition on the line. Callers retain what legitimately differs between them: which
/// user identity to stamp (the cooking user vs. the recorded <c>CookedBy</c>), logging verbosity,
/// <see cref="CookLineResult"/> aggregation, and <c>SaveChanges</c> cadence. The driver never logs and
/// never persists. <see cref="OperationCanceledException"/> always propagates from both methods — a
/// cancelled request must abort the drive, never be recorded as a Shorted / Failed line.
/// </para>
/// </summary>
public sealed class CookLineDriver(IInventoryConsumer consumer, IInventoryProducer producer)
{
    /// <summary>
    /// Drives one Pending <see cref="CookConsumeLine"/>: calls <see cref="IInventoryConsumer.ConsumeAsync"/>
    /// (idempotent via the line's own id as <c>sourceLineRef</c>, plantry-292a) and transitions the line to
    /// <see cref="CookConsumeLineStatus.Applied"/> on success (recording any shortfall),
    /// <see cref="CookConsumeLineStatus.DeferredUnitGap"/> on <see cref="DeferredUnitGapException"/> (no
    /// conversion bridges the unit gap — the consume is owed, never Shorted; plantry-qll2.6), or
    /// <see cref="CookConsumeLineStatus.Shorted"/> on <see cref="InvalidOperationException"/> (the product
    /// has no stock record at all — C8). The returned <see cref="CookConsumeDriveOutcome"/> reports which
    /// branch was taken plus the shortfall amount and its unit, so the caller can aggregate a
    /// <see cref="CookLineResult"/> or log per-line without re-inspecting the line. The DeferredUnitGap catch
    /// MUST precede the InvalidOperationException catch so a unit gap is never mis-recorded as Shorted (see
    /// <see cref="DeferredUnitGapException"/>). <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    public async Task<CookConsumeDriveOutcome> DriveConsumeAsync(
        CookConsumeLine line, Guid cookEventId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var result = await consumer.ConsumeAsync(
                line.ProductId,
                line.Quantity,
                line.UnitId,
                ConsumeReason.Recipe,
                cookEventId,
                userId,
                sourceLineRef: line.Id.Value,
                ct);

            line.MarkApplied(result.ShortfallAmount);
            return new CookConsumeDriveOutcome(
                CookConsumeDriveStatus.Applied, result.ShortfallAmount, result.RequestUnitId);
        }
        catch (OperationCanceledException)
        {
            // A cancelled request aborts the drive — never record it as a resolved (Shorted/Deferred) line.
            throw;
        }
        catch (DeferredUnitGapException)
        {
            // No conversion bridges the ingredient unit to the stock unit (plantry-qll2.6). NOT a shortfall —
            // the pantry is untouched — so the consume is owed until a conversion lands. Caught BEFORE the
            // no-stock catch so the discrimination is preserved (DeferredUnitGapException deliberately does
            // not derive from InvalidOperationException).
            line.MarkDeferredUnitGap();
            return new CookConsumeDriveOutcome(
                CookConsumeDriveStatus.DeferredUnitGap, line.Quantity, line.UnitId);
        }
        catch (InvalidOperationException)
        {
            // No stock record for this product — fully short (C8). Reserved for a genuine no-stock product
            // (never retried), distinct from the deferrable unit gap above.
            line.MarkShorted();
            return new CookConsumeDriveOutcome(
                CookConsumeDriveStatus.Shorted, line.Quantity, line.UnitId);
        }
    }

    /// <summary>
    /// Drives one Pending <see cref="CookProduceLine"/> (yield-on-cook, plantry-854a): calls
    /// <see cref="IInventoryProducer.ProduceAsync"/> (idempotent via the line's own id as
    /// <c>sourceLineRef</c>) and transitions the line to <see cref="CookProduceLineStatus.Applied"/> on
    /// success or the terminal <see cref="CookProduceLineStatus.Failed"/> on
    /// <see cref="InvalidOperationException"/> (the add could not be recorded — unknown product / cannot hold
    /// stock / no location). A failed produce never blocks the cook (mirrors shortfall tolerance, C8/R9). The
    /// returned <see cref="CookProduceDriveOutcome"/> carries the caught exception on failure so the caller
    /// can log it at whatever verbosity it chooses. <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    public async Task<CookProduceDriveOutcome> DriveProduceAsync(
        CookProduceLine line, Guid cookEventId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            await producer.ProduceAsync(
                line.ProductId,
                line.Quantity,
                line.UnitId,
                line.ExpiryDate,
                ProduceReason.Recipe,
                cookEventId,
                userId,
                sourceLineRef: line.Id.Value,
                ct);

            line.MarkApplied();
            return CookProduceDriveOutcome.Applied;
        }
        catch (OperationCanceledException)
        {
            // A cancelled request aborts the drive — never record it as a terminal Failed line.
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // The add could not be recorded — mark Failed (terminal) and hand the exception back so the
            // caller decides how loudly to log; the cook proceeds regardless.
            line.MarkFailed();
            return CookProduceDriveOutcome.Failed(ex);
        }
    }
}

/// <summary>
/// The status branch a <see cref="CookLineDriver.DriveConsumeAsync"/> call resolved to — the single
/// discriminator both callers key their logging/aggregation off, mirroring the terminal
/// <see cref="CookConsumeLineStatus"/> the line was transitioned to.
/// </summary>
public enum CookConsumeDriveStatus
{
    /// <summary>Consume ran; line is <see cref="CookConsumeLineStatus.Applied"/> (possibly with shortfall).</summary>
    Applied,

    /// <summary>No conversion bridged the unit gap; line is <see cref="CookConsumeLineStatus.DeferredUnitGap"/> (owed).</summary>
    DeferredUnitGap,

    /// <summary>Product had no stock record; line is <see cref="CookConsumeLineStatus.Shorted"/> (never retried).</summary>
    Shorted,
}

/// <summary>
/// The outcome of a <see cref="CookLineDriver.DriveConsumeAsync"/> call. <see cref="ShortfallAmount"/> and
/// <see cref="ShortfallUnitId"/> are the reported shortfall in the requested unit for
/// <see cref="CookConsumeDriveStatus.Applied"/>, and the full requested quantity / line unit for a
/// <see cref="CookConsumeDriveStatus.DeferredUnitGap"/> or <see cref="CookConsumeDriveStatus.Shorted"/> line
/// (nothing was deducted) — matching what each caller previously computed inline.
/// </summary>
public sealed record CookConsumeDriveOutcome(
    CookConsumeDriveStatus Status,
    decimal ShortfallAmount,
    Guid ShortfallUnitId);

/// <summary>The status branch a <see cref="CookLineDriver.DriveProduceAsync"/> call resolved to.</summary>
public enum CookProduceDriveStatus
{
    /// <summary>Produce ran; line is <see cref="CookProduceLineStatus.Applied"/>.</summary>
    Applied,

    /// <summary>Add could not be recorded; line is the terminal <see cref="CookProduceLineStatus.Failed"/>.</summary>
    Failed,
}

/// <summary>
/// The outcome of a <see cref="CookLineDriver.DriveProduceAsync"/> call. <see cref="Failure"/> is the caught
/// <see cref="InvalidOperationException"/> for a <see cref="CookProduceDriveStatus.Failed"/> line (so the
/// caller can log it) and <c>null</c> for <see cref="CookProduceDriveStatus.Applied"/>.
/// </summary>
public sealed record CookProduceDriveOutcome(
    CookProduceDriveStatus Status,
    Exception? Failure)
{
    /// <summary>The shared Applied outcome (no failure to carry).</summary>
    public static CookProduceDriveOutcome Applied { get; } = new(CookProduceDriveStatus.Applied, null);

    /// <summary>Builds a Failed outcome carrying the exception that caused the produce to fail.</summary>
    public static CookProduceDriveOutcome Failed(Exception failure) =>
        new(CookProduceDriveStatus.Failed, failure);
}
