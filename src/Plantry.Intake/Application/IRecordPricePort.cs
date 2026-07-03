namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Pricing to record a price observation during commit.
/// Implemented in Plantry.Web (adapter over Pricing's RecordObservationCommand).
/// Returns the PriceObservationId (as Guid) of the created observation.
/// </summary>
public interface IRecordPricePort
{
    Task<Guid> RecordAsync(
        Guid productId,
        Guid? skuId,
        decimal price,
        decimal quantity,
        Guid unitId,
        string? merchantText,
        Guid? storeId,
        Guid sourceRef,
        DateTimeOffset observedAt,
        Guid userId,
        CancellationToken ct = default);
}
