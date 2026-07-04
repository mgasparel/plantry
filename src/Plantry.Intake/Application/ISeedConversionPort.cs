namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Catalog to seed a per-product AI-suggested unit conversion learned during
/// intake commit (plantry-1mu). When the user commits a weight-priced line as an each-count, the
/// household's grocer-specific weight→each factor (e.g. "1 lb bananas ≈ 5 each") is recorded on the
/// Product aggregate tagged <c>AiSuggested</c> (plantry-3k44 provenance) so future receipts for the same
/// product pre-fill deterministically. Implemented in Plantry.Web over Catalog's Product aggregate.
///
/// <para>Idempotent per plantry-3k44's merge rule: a suggestion never overwrites an existing conversion
/// for the same (from,to) pair, so re-commits and repeat receipts do not duplicate or fight the factor.</para>
/// </summary>
public interface ISeedConversionPort
{
    /// <summary>Seeds an AI-suggested <paramref name="factor"/> converting <paramref name="fromUnitId"/>
    /// (the receipt weight unit) to <paramref name="toUnitId"/> (the each unit) on the given product.</summary>
    Task SeedAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default);
}
