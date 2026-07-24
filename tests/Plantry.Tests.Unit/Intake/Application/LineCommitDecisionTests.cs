using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1 decision-table tests for <see cref="LineCommitDecision"/> — the pure line-commit core extracted from
/// <see cref="CommitSessionCommand"/> (plantry-tjl2.1). No ports, no fakes: the whole point of the
/// extraction is that both decisions are exercised directly. Covers the three price-observation outcomes
/// (plantry-1mu / plantry-x7j0 Fix B) and every predicate flip of the five-gate weight→each seed condition
/// (plantry-x7j0 Fix A).
/// </summary>
public sealed class LineCommitDecisionTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private readonly Guid _lbUnitId = Guid.CreateVersion7();
    private readonly Guid _kgUnitId = Guid.CreateVersion7();
    private readonly Guid _eachUnitId = Guid.CreateVersion7();
    private readonly Guid _unknownUnitId = Guid.CreateVersion7(); // deliberately absent from the lookup

    private ReviewReferenceLookup Lookup() =>
        new(new ReviewReferenceData(
            Products: [],
            Units:
            [
                new ReviewUnitOption(_lbUnitId, "lb", "Pound", ReviewUnitDimension.Mass),
                new ReviewUnitOption(_kgUnitId, "kg", "Kilogram", ReviewUnitDimension.Mass),
                new ReviewUnitOption(_eachUnitId, "each", "Each", ReviewUnitDimension.Count),
            ],
            Locations: [],
            Categories: [],
            Stores: []));

    private ImportSession NewSession() =>
        ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);

    // ── ReviewReferenceLookup ───────────────────────────────────────────────────────

    [Fact]
    public void Lookup_Resolves_A_Known_Weight_Label_Case_Insensitively()
    {
        var lookup = Lookup();

        Assert.True(lookup.TryResolveWeightUnit("LB", out var unitId));
        Assert.Equal(_lbUnitId, unitId);
    }

    [Fact]
    public void Lookup_Does_Not_Resolve_An_Unknown_Weight_Label()
    {
        var lookup = Lookup();

        Assert.False(lookup.TryResolveWeightUnit("oz", out _));
    }

    [Fact]
    public void Lookup_Resolves_A_Known_Unit_Dimension_And_Rejects_An_Unknown_Unit()
    {
        var lookup = Lookup();

        Assert.True(lookup.TryGetDimension(_eachUnitId, out var dimension));
        Assert.Equal(ReviewUnitDimension.Count, dimension);
        Assert.False(lookup.TryGetDimension(_unknownUnitId, out _));
    }

    // ── Price-observation decision — the three outcomes ──────────────────────────────

    [Fact]
    public void Price_Is_NoPrice_When_The_Line_Has_No_Price()
    {
        var session = NewSession();
        var line = session.AddLine(1, "Free sample", SuggestedConfidence.High, null);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _eachUnitId, _locationId, null, price: null);

        var decision = LineCommitDecision.DecidePriceObservation(line, weightUnitId: null);

        Assert.IsType<PriceObservationDecision.NoPrice>(decision);
    }

    [Fact]
    public void Price_Skips_When_A_Weight_Line_Has_A_Price_But_The_Weight_Unit_Did_Not_Resolve()
    {
        // Receipt weight in "oz" — a label no household unit matches. weightUnitId is null: recording would
        // fall back to the committed each-unit ($/each), which plantry-1mu forbids, so it is skipped.
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "BULK ALMONDS 12 oz", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 12m, suggestedUnitLabel: "oz", suggestedPrice: 5.00m,
            receiptWeight: 12m, receiptWeightUnitLabel: "oz",
            estimatedEachCount: 3m, estimatedEachConfidence: SuggestedConfidence.High);
        line.Confirm(productId, null, 3m, _eachUnitId, _locationId, null, price: 5.00m);

        var decision = LineCommitDecision.DecidePriceObservation(line, weightUnitId: null);

        Assert.IsType<PriceObservationDecision.SkipUnresolvedWeightUnit>(decision);
    }

    [Fact]
    public void Price_Records_A_Weight_Line_In_Its_Receipt_Weight_Unit_Not_The_Committed_Each()
    {
        // Accepted an each-count, but pricing observes in the receipt's TRUE unit (lb), never $/each.
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
        line.Confirm(productId, null, 7m, _eachUnitId, _locationId, null, price: 0.79m);

        var decision = LineCommitDecision.DecidePriceObservation(line, weightUnitId: _lbUnitId);

        var record = Assert.IsType<PriceObservationDecision.Record>(decision);
        Assert.Equal(0.79m, record.Price);
        Assert.Equal(1.34m, record.Quantity);   // the true weight, not the 7-each count
        Assert.Equal(_lbUnitId, record.UnitId);  // $/lb — never a $/each observation
    }

    [Fact]
    public void Price_Records_A_Non_Weight_Line_In_Its_Committed_Quantity_And_Unit()
    {
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "EGGS DOZEN", SuggestedConfidence.High, null,
            suggestedProductId: productId, suggestedQuantity: 2m, suggestedUnitLabel: "each", suggestedPrice: 3.50m);
        line.Confirm(productId, null, 2m, _eachUnitId, _locationId, null, price: 3.50m);

        var decision = LineCommitDecision.DecidePriceObservation(line, weightUnitId: null);

        var record = Assert.IsType<PriceObservationDecision.Record>(decision);
        Assert.Equal(3.50m, record.Price);
        Assert.Equal(2m, record.Quantity);        // committed quantity
        Assert.Equal(_eachUnitId, record.UnitId);  // committed unit
    }

    // ── Price-amendment decision (ADR-023 A8) — the three outcomes ──────────────────

    [Fact]
    public void Amend_Is_NoObservation_When_The_Line_Never_Recorded_A_Price()
    {
        var session = NewSession();
        var line = session.AddLine(1, "Free sample", SuggestedConfidence.High, null);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _eachUnitId, _locationId, null, price: null);
        line.MarkCommitted(Guid.CreateVersion7(), null);

        var decision = LineCommitDecision.DecidePriceAmendment(line, correctedQuantity: 5m);

        Assert.IsType<AmendPriceDecision.NoObservation>(decision);
    }

    [Fact]
    public void Amend_Skips_A_Weight_Priced_Line_Even_Though_Its_EachCount_Was_Corrected()
    {
        // spec acceptance #5: an each-count fix on a weight-priced line (plantry-1mu) must leave the
        // weight-denominated observation untouched — the observation's quantity is the FIXED receipt
        // weight, never the corrected committed each-count.
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
        line.Confirm(productId, null, 7m, _eachUnitId, _locationId, null, price: 0.79m);
        line.MarkCommitted(Guid.CreateVersion7(), Guid.CreateVersion7());

        var decision = LineCommitDecision.DecidePriceAmendment(line, correctedQuantity: 9m); // 7 -> 9 each

        Assert.IsType<AmendPriceDecision.SkipWeightDenominated>(decision);
    }

    [Fact]
    public void Amend_Amends_A_Non_Weight_Line_With_The_Corrected_Quantity()
    {
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "ONIONS YELLOW", SuggestedConfidence.High, null,
            suggestedProductId: productId, suggestedQuantity: 1m, suggestedUnitLabel: "lb", suggestedPrice: 3.98m);
        line.Confirm(productId, null, 1m, _lbUnitId, _locationId, null, price: 3.98m);
        line.MarkCommitted(Guid.CreateVersion7(), Guid.CreateVersion7());

        var decision = LineCommitDecision.DecidePriceAmendment(line, correctedQuantity: 3m);

        var amend = Assert.IsType<AmendPriceDecision.Amend>(decision);
        Assert.Equal(3m, amend.CorrectedQuantity);
    }

    // ── Conversion-seed decision — the five-gate decision table ──────────────────────

    /// <summary>
    /// Builds a confirmed weight-priced line for the seed decision. Each toggle isolates one gate:
    /// <paramref name="newProduct"/> flips gate 1 (existing product), <paramref name="hasEach"/> flips gate
    /// 2 (an accepted each estimate), <paramref name="receiptWeight"/> flips gate 5 (positive weight — pass
    /// 0), and <paramref name="committedUnit"/> flips gate 4 (committed dimension is Count). Gate 3 (weight
    /// unit resolved) is flipped by the <c>weightUnitId</c> argument to the decision itself.
    /// </summary>
    private ImportLine SeedLine(
        ImportSession session, Guid productId,
        bool newProduct = false, bool hasEach = true, decimal receiptWeight = 1.34m, Guid? committedUnit = null)
    {
        var unit = committedUnit ?? _eachUnitId;
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: receiptWeight, receiptWeightUnitLabel: "lb",
            estimatedEachCount: hasEach ? 7m : null,
            estimatedEachConfidence: hasEach ? SuggestedConfidence.High : null);
        // Committed each-count = 7 → factor 7 / 1.34 when all gates hold.
        if (newProduct)
            line.ConfirmAsNew("Backyard Bananas", Guid.CreateVersion7(), 7m, unit, _locationId, null, 0.79m);
        else
            line.Confirm(productId, null, 7m, unit, _locationId, null, 0.79m);
        return line;
    }

    [Fact]
    public void Seed_Is_Produced_With_The_Correct_Factor_When_Every_Gate_Holds()
    {
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = SeedLine(session, productId); // existing product, each estimate, each unit, weight 1.34

        var decision = LineCommitDecision.DecideConversionSeed(line, weightUnitId: _lbUnitId, lookup: Lookup());

        var seed = Assert.IsType<ConversionSeedDecision.Seed>(decision);
        Assert.Equal(_lbUnitId, seed.FromUnitId);   // from the receipt weight unit
        Assert.Equal(_eachUnitId, seed.ToUnitId);   // to the accepted each unit
        Assert.Equal(7m / 1.34m, seed.Factor);      // each per lb, from confirmed count / preserved weight
    }

    public enum SeedGate
    {
        NewProduct,             // gate 1 false — brand-new product
        NoEachEstimate,         // gate 2 false — no accepted each-count
        WeightUnitUnresolved,   // gate 3 false — weightUnitId null
        CommittedUnitIsWeight,  // gate 4 false — committed unit's dimension is Mass, not Count
        CommittedUnitUnknown,   // gate 4 false — committed unit absent from the lookup
        ZeroReceiptWeight,      // gate 5 false — receipt weight is 0 (would divide by zero)
    }

    [Theory]
    [InlineData(SeedGate.NewProduct)]
    [InlineData(SeedGate.NoEachEstimate)]
    [InlineData(SeedGate.WeightUnitUnresolved)]
    [InlineData(SeedGate.CommittedUnitIsWeight)]
    [InlineData(SeedGate.CommittedUnitUnknown)]
    [InlineData(SeedGate.ZeroReceiptWeight)]
    public void Seed_Is_None_When_Any_Single_Gate_Fails(SeedGate broken)
    {
        var session = NewSession();
        var productId = Guid.CreateVersion7();

        // Start from an all-gates-hold line and flip exactly the one gate under test.
        var line = SeedLine(
            session, productId,
            newProduct: broken == SeedGate.NewProduct,
            hasEach: broken != SeedGate.NoEachEstimate,
            receiptWeight: broken == SeedGate.ZeroReceiptWeight ? 0m : 1.34m,
            committedUnit: broken switch
            {
                SeedGate.CommittedUnitIsWeight => _kgUnitId,        // Mass — a different weight unit
                SeedGate.CommittedUnitUnknown => _unknownUnitId,    // not in the lookup
                _ => _eachUnitId,
            });

        var weightUnitId = broken == SeedGate.WeightUnitUnresolved ? (Guid?)null : _lbUnitId;

        var decision = LineCommitDecision.DecideConversionSeed(line, weightUnitId, lookup: Lookup());

        Assert.IsType<ConversionSeedDecision.None>(decision);
    }

    [Fact]
    public void Seed_Is_None_When_The_Lookup_Is_Absent()
    {
        // Defensive totality: in production the lookup is loaded together with the weight unit, but the pure
        // decision must still be total if handed a null lookup.
        var session = NewSession();
        var productId = Guid.CreateVersion7();
        var line = SeedLine(session, productId);

        var decision = LineCommitDecision.DecideConversionSeed(line, weightUnitId: _lbUnitId, lookup: null);

        Assert.IsType<ConversionSeedDecision.None>(decision);
    }
}
