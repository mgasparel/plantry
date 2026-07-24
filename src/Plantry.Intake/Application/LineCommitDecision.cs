using Plantry.Intake.Domain;

namespace Plantry.Intake.Application;

/// <summary>
/// A pure, mock-free view over an already-fetched <see cref="ReviewReferenceData"/> exposing the two
/// lookups the commit-time line decisions need: a receipt-weight-label → <c>UnitId</c> resolution (so a
/// weight-priced line's price observation stays in the receipt's TRUE unit, plantry-1mu) and a
/// <c>UnitId</c> → dimension resolution (so a conversion is only ever seeded when the committed unit is
/// Count, never a bogus weight→weight factor, plantry-x7j0 Fix A).
///
/// <para>Both maps are built once in the constructor from data the caller already loaded — the wrapper
/// issues <b>zero</b> IO. <see cref="CommitSessionCommand"/> keeps ownership of *when* the reference data
/// is fetched (lazily, at most once per commit, and only for a line that actually carries a receipt
/// weight + label): this type never fetches, it only reads what it was handed.</para>
/// </summary>
public sealed class ReviewReferenceLookup
{
    private readonly IReadOnlyDictionary<string, Guid> _unitIdByLabel;
    private readonly IReadOnlyDictionary<Guid, ReviewUnitDimension> _dimensionByUnitId;

    public ReviewReferenceLookup(ReviewReferenceData reference)
    {
        // Case-insensitive weight-unit-label → UnitId ("lb"/"kg" → the unit the price observation records
        // in). Last-wins; unit codes are unique per household.
        var byLabel = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        // UnitId → dimension, so the conversion-seed gate can require the committed unit be Count before
        // learning a weight→each factor. Last-wins; unit ids are unique per household.
        var byUnitId = new Dictionary<Guid, ReviewUnitDimension>();
        foreach (var unit in reference.Units)
        {
            byLabel[unit.Code] = unit.Id;
            byUnitId[unit.Id] = unit.Dimension;
        }
        _unitIdByLabel = byLabel;
        _dimensionByUnitId = byUnitId;
    }

    /// <summary>Resolves a receipt weight label ("lb"/"kg") to the household <c>UnitId</c> it names, if any.</summary>
    public bool TryResolveWeightUnit(string label, out Guid unitId) =>
        _unitIdByLabel.TryGetValue(label, out unitId);

    /// <summary>Resolves a household <c>UnitId</c> to its measurement dimension, if the unit is known.</summary>
    public bool TryGetDimension(Guid unitId, out ReviewUnitDimension dimension) =>
        _dimensionByUnitId.TryGetValue(unitId, out dimension);
}

/// <summary>
/// The price-observation outcome for a single commit line (plantry-1mu / plantry-x7j0 Fix B). Exactly one
/// of: <see cref="NoPrice"/> (the line carries no price), <see cref="SkipUnresolvedWeightUnit"/> (the line
/// carries a receipt weight whose unit label did not resolve — recording would fall back to the committed
/// unit and produce a wrong-unit price, worse than a missing one, so the observation is skipped and
/// logged), or <see cref="Record"/> (observe <see cref="Record.Price"/> for <see cref="Record.Quantity"/>
/// of <see cref="Record.UnitId"/> — a weight-carrying line records in the receipt's TRUE weight unit, a
/// non-weight line in its committed quantity/unit).
/// </summary>
public abstract record PriceObservationDecision
{
    private PriceObservationDecision() { }

    /// <summary>The line has no price — nothing to observe.</summary>
    public sealed record NoPrice : PriceObservationDecision;

    /// <summary>The line carries a receipt weight whose unit label did not resolve to a household unit;
    /// the observation is skipped to avoid recording a wrong-unit price (stock/conversion unaffected).</summary>
    public sealed record SkipUnresolvedWeightUnit : PriceObservationDecision;

    /// <summary>Record a price observation of <paramref name="Price"/> for <paramref name="Quantity"/> of
    /// <paramref name="UnitId"/>.</summary>
    public sealed record Record(decimal Price, decimal Quantity, Guid UnitId) : PriceObservationDecision;
}

/// <summary>
/// The price-amendment outcome for a corrected purchase line (ADR-023 A8, plantry-hitc). Exactly one of:
/// <see cref="NoObservation"/> (the line never recorded a price observation at commit — nothing to
/// amend), <see cref="SkipWeightDenominated"/> (the line's observation was recorded against its fixed
/// receipt weight, not its committed quantity — the corrected quantity does not feed it, so it must be
/// left untouched: an each-count fix on a weight-priced line, plantry-1mu), or <see cref="Amend"/>
/// (the observation's quantity IS the committed quantity, so the corrected value replaces it).
/// </summary>
public abstract record AmendPriceDecision
{
    private AmendPriceDecision() { }

    /// <summary>The line never recorded a price observation — nothing to amend.</summary>
    public sealed record NoObservation : AmendPriceDecision;

    /// <summary>The observation is denominated in the line's fixed receipt weight, not its committed
    /// (correctable) quantity — untouched per A8.</summary>
    public sealed record SkipWeightDenominated : AmendPriceDecision;

    /// <summary>Re-derive the observation for <paramref name="CorrectedQuantity"/>.</summary>
    public sealed record Amend(decimal CorrectedQuantity) : AmendPriceDecision;
}

/// <summary>
/// The weight→each conversion-seed outcome for a single commit line (plantry-1mu / plantry-x7j0 Fix A).
/// Either <see cref="None"/> (one of the five gates fails, nothing is learned) or <see cref="Seed"/>
/// (learn <see cref="Seed.Factor"/> as the household's <see cref="Seed.FromUnitId"/> →
/// <see cref="Seed.ToUnitId"/> factor).
/// </summary>
public abstract record ConversionSeedDecision
{
    private ConversionSeedDecision() { }

    /// <summary>No conversion is learned from this line.</summary>
    public sealed record None : ConversionSeedDecision;

    /// <summary>Seed <paramref name="Factor"/> as the <paramref name="FromUnitId"/> →
    /// <paramref name="ToUnitId"/> conversion for the product.</summary>
    public sealed record Seed(Guid FromUnitId, Guid ToUnitId, decimal Factor) : ConversionSeedDecision;
}

/// <summary>
/// The PURE line-commit decision core lifted out of <see cref="CommitSessionCommand"/>.ExecuteAsync
/// (plantry-tjl2.1) — the same decomposition pattern the Recipes structural-health epic applied to its
/// orchestrators (e.g. <c>IngredientOrdinalMerge</c>). Given a confirmed line, the receipt weight unit the
/// caller resolved for it, and a <see cref="ReviewReferenceLookup"/>, it decides the line's price
/// observation and its weight→each conversion seed. It issues <b>zero</b> IO and holds no state, so both
/// decisions are directly unit-testable as a decision table without standing up the orchestrator or any
/// port fakes.
/// </summary>
public static class LineCommitDecision
{
    /// <summary>
    /// Decides how a line's price observation is recorded (plantry-1mu / plantry-x7j0 Fix B). Evaluated in
    /// order:
    /// <list type="number">
    ///   <item>no price on the line → <see cref="PriceObservationDecision.NoPrice"/>;</item>
    ///   <item>the line carries a receipt weight but its unit did not resolve
    ///     (<paramref name="weightUnitId"/> is null) → <see cref="PriceObservationDecision.SkipUnresolvedWeightUnit"/>
    ///     — recording would fall back to the committed unit and pollute pricing history with a wrong-unit
    ///     ($/each) observation, so it is skipped;</item>
    ///   <item>otherwise <see cref="PriceObservationDecision.Record"/> — a weight-carrying line observes in
    ///     the receipt's TRUE weight unit (<paramref name="weightUnitId"/>, guaranteed non-null here), a
    ///     non-weight line in its committed quantity/unit.</item>
    /// </list>
    /// </summary>
    public static PriceObservationDecision DecidePriceObservation(ImportLine line, Guid? weightUnitId)
    {
        if (line.Price is not { } price)
            return new PriceObservationDecision.NoPrice();

        if (line.ReceiptWeight is not null && weightUnitId is null)
            return new PriceObservationDecision.SkipUnresolvedWeightUnit();

        // weightUnitId is guaranteed non-null for a weight-carrying line — the unresolved case returned above.
        return line.ReceiptWeight is { } weight
            ? new PriceObservationDecision.Record(price, weight, weightUnitId!.Value)
            : new PriceObservationDecision.Record(price, line.Quantity!.Value, line.UnitId!.Value);
    }

    /// <summary>
    /// Decides whether — and how — a purchase-entry amendment (ADR-023 A8, plantry-hitc) re-derives this
    /// line's price observation for <paramref name="correctedQuantity"/>. Mirrors
    /// <see cref="DecidePriceObservation"/>'s own unit choice rather than re-running it wholesale: at commit,
    /// a line carrying a receipt weight (<see cref="ImportLine.ReceiptWeight"/>) observes in that FIXED
    /// weight, never in the committed (correctable) quantity — so correcting the committed each-count can
    /// never feed that observation, no matter what the corrected value is. A line with no receipt weight
    /// observes directly in its committed quantity/unit, so the correction replaces it 1:1.
    /// </summary>
    public static AmendPriceDecision DecidePriceAmendment(ImportLine line, decimal correctedQuantity)
    {
        if (line.PriceObservationId is null)
            return new AmendPriceDecision.NoObservation();

        if (line.ReceiptWeight is not null)
            return new AmendPriceDecision.SkipWeightDenominated();

        return new AmendPriceDecision.Amend(correctedQuantity);
    }

    /// <summary>
    /// Decides whether committing this line learns the household's weight→each factor (plantry-1mu /
    /// plantry-x7j0 Fix A). Returns <see cref="ConversionSeedDecision.Seed"/> only when ALL five gates hold,
    /// else <see cref="ConversionSeedDecision.None"/>:
    /// <list type="number">
    ///   <item><b>existing product</b> — <c>!line.IsNewProduct</c> (a brand-new product has no prior each
    ///     estimate to reconcile);</item>
    ///   <item><b>has an each estimate</b> — <c>line.HasEachEstimate</c> (the user accepted an estimated
    ///     each-count for a weight-priced line);</item>
    ///   <item><b>receipt weight unit resolved</b> — <paramref name="weightUnitId"/> is non-null (the
    ///     conversion anchor);</item>
    ///   <item><b>committed unit is Count</b> — the committed <c>UnitId</c>'s dimension resolves to
    ///     <see cref="ReviewUnitDimension.Count"/> via <paramref name="lookup"/>. Committing in a
    ///     <em>different weight</em> unit (0.6 kg on an lb receipt) must NOT seed a quantity-derived
    ///     "lb→kg" factor — cross-weight conversion is a fixed physical constant, never receipt-derived;</item>
    ///   <item><b>positive receipt weight</b> — <c>line.ReceiptWeight &gt; 0</c> (the factor's divisor).</item>
    /// </list>
    /// The factor is <c>line.Quantity / line.ReceiptWeight</c> (each per weight unit).
    /// </summary>
    public static ConversionSeedDecision DecideConversionSeed(
        ImportLine line, Guid? weightUnitId, ReviewReferenceLookup? lookup)
    {
        if (line.IsNewProduct)
            return new ConversionSeedDecision.None();
        if (!line.HasEachEstimate)
            return new ConversionSeedDecision.None();
        if (weightUnitId is not { } fromUnit)
            return new ConversionSeedDecision.None();
        // The lookup is loaded together with the weight unit, so it is non-null whenever weightUnitId is;
        // guard defensively so the pure decision is total.
        if (lookup is null || !lookup.TryGetDimension(line.UnitId!.Value, out var committedDimension)
            || committedDimension != ReviewUnitDimension.Count)
            return new ConversionSeedDecision.None();
        if (line.ReceiptWeight is not { } receiptWeight || receiptWeight <= 0m)
            return new ConversionSeedDecision.None();

        var factor = line.Quantity!.Value / receiptWeight; // each per weight unit
        return new ConversionSeedDecision.Seed(fromUnit, line.UnitId!.Value, factor);
    }
}
