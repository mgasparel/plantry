using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

using CatalogUnit = Plantry.Catalog.Domain.Unit;

/// <summary>
/// L1 unit tests for <see cref="QuantityDisplay"/> — the pure fraction-rendering + unit-simplification
/// formatter (quantity-display.md §3–§5). The §5 golden table is the normative contract and is
/// reproduced here verbatim, alongside the tolerance-boundary, tie-break, integer-ratio-firewall,
/// Decimal-passthrough, and zero/negative cases §5 and §8 call out.
///
/// The seeded US relationships (§5): 3 tsp = 1 tbsp, 16 tbsp = 1 cup. The <see cref="Units"/> below use
/// factor-to-base values that are <b>exact</b> integer multiples (tsp 4.92892 → tbsp 14.78676 → cup
/// 236.58816) so those ratios hold precisely. ml / g are <see cref="DisplayStyle.Decimal"/> and sit at
/// non-integer ratios to the spoon family — the metric/imperial firewall (Q5). NOTE: the production
/// seeder (CatalogReferenceDataSeeder) currently seeds cup = 240 and tbsp = 14.7868, which are NOT exact
/// multiples (240 / 14.7868 = 16.23), so tbsp→cup would not simplify against real data — tracked for the
/// vci8.3 wiring pass; QuantityDisplay itself is unit-agnostic and correct given exact-ratio units.
/// </summary>
public sealed class QuantityDisplayTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();

    private static CatalogUnit MakeUnit(string code, Dimension dimension, decimal factorToBase,
        DisplayStyle style, UnitSystem system, bool isBase = false)
    {
        var unit = CatalogUnit.Create(HouseholdId, code, code, dimension, factorToBase, isBase, system);
        unit.SetDisplayStyle(style);
        return unit;
    }

    // Exact-ratio spoon family (3 tsp = 1 tbsp, 16 tbsp = 1 cup), tagged UsCustomary, plus Metric
    // Decimal units. The metric/imperial firewall (Q5) is now the explicit UnitSystem tag.
    private static readonly CatalogUnit Ml   = MakeUnit("ml",   Dimension.Volume, 1m,         DisplayStyle.Decimal,  UnitSystem.Metric, isBase: true);
    private static readonly CatalogUnit Tsp  = MakeUnit("tsp",  Dimension.Volume, 4.92892m,   DisplayStyle.Fraction, UnitSystem.UsCustomary);
    private static readonly CatalogUnit Tbsp = MakeUnit("tbsp", Dimension.Volume, 14.78676m,  DisplayStyle.Fraction, UnitSystem.UsCustomary); // 3 x tsp
    private static readonly CatalogUnit Cup  = MakeUnit("cup",  Dimension.Volume, 236.58816m, DisplayStyle.Fraction, UnitSystem.UsCustomary); // 16 x tbsp
    private static readonly CatalogUnit Gram = MakeUnit("g",    Dimension.Mass,   1m,         DisplayStyle.Decimal,  UnitSystem.Metric, isBase: true);

    private static readonly IReadOnlyList<CatalogUnit> Units = [Ml, Tsp, Tbsp, Cup, Gram];

    /// <summary>Runs Simplify at the given scale (skipped at 1×, per Q4) then FormatAmount, mirroring the
    /// render pipeline the golden table describes; returns the displayed string including unit code.</summary>
    private static string Render(decimal authoredAmount, CatalogUnit authoredUnit, decimal scale)
    {
        var scaled = authoredAmount * scale;
        var (amount, unitId) = scale == 1m
            ? (scaled, authoredUnit.Id.Value)
            : QuantityDisplay.Simplify(scaled, authoredUnit.Id.Value, Units);

        var unit = Units.Single(u => u.Id.Value == unitId);
        return $"{QuantityDisplay.FormatAmount(amount, unit.DisplayStyle)} {unit.Code}";
    }

    // ── §5 golden table (normative, verbatim) ──────────────────────────────────────

    [Fact] public void Golden_Half_Cup_At_1x_Renders_Half_Cup()        => Assert.Equal("½ cup",   Render(0.5m,   Cup,  1m));
    [Fact] public void Golden_Third_Cup_Tolerance_Absorbs_Decimal()    => Assert.Equal("⅓ cup",   Render(0.333m, Cup,  1m));
    [Fact] public void Golden_Point3_Cup_Is_Not_Third_Decimal_Fallback() => Assert.Equal("0.3 cup", Render(0.3m,  Cup,  1m));
    [Fact] public void Golden_4Tbsp_At_1x_Keeps_Authored_Unit()        => Assert.Equal("4 tbsp",  Render(4m,     Tbsp, 1m));
    [Fact] public void Golden_2Tbsp_At_2x_Becomes_QuarterCup()         => Assert.Equal("¼ cup",   Render(2m,     Tbsp, 2m));
    [Fact] public void Golden_2Tbsp_At_1p5x_Stays_3Tbsp()              => Assert.Equal("3 tbsp",  Render(2m,     Tbsp, 1.5m));
    [Fact] public void Golden_8Tbsp_At_2x_Becomes_1Cup()              => Assert.Equal("1 cup",   Render(8m,     Tbsp, 2m));
    [Fact] public void Golden_1Tbsp_At_Half_Becomes_HalfTbsp()        => Assert.Equal("½ tbsp",  Render(1m,     Tbsp, 0.5m));
    [Fact] public void Golden_1Tsp_At_2x_Stays_2Tsp()                => Assert.Equal("2 tsp",   Render(1m,     Tsp,  2m));
    [Fact] public void Golden_2Tsp_At_3x_Becomes_2Tbsp()             => Assert.Equal("2 tbsp",  Render(2m,     Tsp,  3m));
    [Fact] public void Golden_1Tsp_At_3x_Becomes_1Tbsp()             => Assert.Equal("1 tbsp",  Render(1m,     Tsp,  3m));
    [Fact] public void Golden_1Tsp_At_5x_Stays_5Tsp()               => Assert.Equal("5 tsp",   Render(1m,     Tsp,  5m));
    [Fact] public void Golden_QuarterCup_At_3x_Becomes_ThreeQuarterCup() => Assert.Equal("¾ cup", Render(0.25m, Cup, 3m));
    [Fact] public void Golden_250Ml_At_2x_Stays_500Ml()             => Assert.Equal("500 ml",  Render(250m,   Ml,   2m));
    [Fact] public void Golden_100g_At_1p37x_Stays_137g()           => Assert.Equal("137 g",   Render(100m,   Gram, 1.37m));
    [Fact] public void Golden_HalfCup_At_1p1x_Decimal_Fallback()   => Assert.Equal("0.55 cup", Render(0.5m,  Cup,  1.1m));

    // ── FormatAmount: fraction glyphs & mixed numbers (§3) ─────────────────────────

    [Theory]
    [InlineData(0.5,   "½")]
    [InlineData(0.25,  "¼")]
    [InlineData(0.75,  "¾")]
    [InlineData(0.125, "⅛")]
    [InlineData(0.375, "⅜")]
    [InlineData(0.625, "⅝")]
    [InlineData(0.875, "⅞")]
    public void FormatAmount_Fraction_SnapsBareGlyph(decimal amount, string expected)
        => Assert.Equal(expected, QuantityDisplay.FormatAmount(amount, DisplayStyle.Fraction));

    [Fact]
    public void FormatAmount_Fraction_ThirdsSnap()
    {
        Assert.Equal("⅓", QuantityDisplay.FormatAmount(1m / 3m, DisplayStyle.Fraction));
        Assert.Equal("⅔", QuantityDisplay.FormatAmount(2m / 3m, DisplayStyle.Fraction));
    }

    [Theory]
    [InlineData(1.5,  "1½")]
    [InlineData(1.75, "1¾")]
    [InlineData(2.25, "2¼")]
    public void FormatAmount_Fraction_MixedNumber_NoSeparator(decimal amount, string expected)
        => Assert.Equal(expected, QuantityDisplay.FormatAmount(amount, DisplayStyle.Fraction));

    [Fact]
    public void FormatAmount_Fraction_WholeNumber_RendersInteger()
    {
        Assert.Equal("4", QuantityDisplay.FormatAmount(4m, DisplayStyle.Fraction));
        Assert.Equal("1", QuantityDisplay.FormatAmount(1m, DisplayStyle.Fraction));
    }

    [Fact]
    public void FormatAmount_Fraction_RemainderNearWhole_CarriesUp()
    {
        // 1.997 is within tolerance of 2 → carries to a bare "2", not "1" + a glyph.
        Assert.Equal("2", QuantityDisplay.FormatAmount(1.997m, DisplayStyle.Fraction));
        // 2.004 snaps down to "2".
        Assert.Equal("2", QuantityDisplay.FormatAmount(2.004m, DisplayStyle.Fraction));
    }

    // ── Tolerance boundary around ⅓ (§5 / §8) ─────────────────────────────────────

    [Fact]
    public void FormatAmount_Fraction_ToleranceBoundary_Around_Third()
    {
        // 0.34 is within 0.01 of ⅓ (0.3333…) → snaps; 0.32 is 0.0133 away → decimal fallback.
        Assert.Equal("⅓",    QuantityDisplay.FormatAmount(0.34m, DisplayStyle.Fraction));
        Assert.Equal("0.32", QuantityDisplay.FormatAmount(0.32m, DisplayStyle.Fraction));
    }

    [Fact]
    public void FormatAmount_Fraction_NonSnapping_FallsBackToDecimal()
    {
        // 0.6 is not near any vocabulary fraction (⅝ = 0.625 is 0.025 away) → whole amount as 0.###.
        Assert.Equal("0.6",  QuantityDisplay.FormatAmount(0.6m, DisplayStyle.Fraction));
        Assert.Equal("0.55", QuantityDisplay.FormatAmount(0.55m, DisplayStyle.Fraction));
    }

    // ── Decimal passthrough (§8) ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5,  "0.5")]
    [InlineData(0.25, "0.25")]
    [InlineData(4,    "4")]
    [InlineData(137,  "137")]
    [InlineData(0.333, "0.333")]
    public void FormatAmount_Decimal_NeverSnaps(decimal amount, string expected)
        => Assert.Equal(expected, QuantityDisplay.FormatAmount(amount, DisplayStyle.Decimal));

    // ── Zero / negative are untouched by the feature (§3.3) ────────────────────────

    [Fact]
    public void FormatAmount_ZeroAndNegative_UntouchedByFraction()
    {
        Assert.Equal("0", QuantityDisplay.FormatAmount(0m, DisplayStyle.Fraction));
        Assert.Equal("0", QuantityDisplay.FormatAmount(0m, DisplayStyle.Decimal));
        Assert.Equal("-0.5", QuantityDisplay.FormatAmount(-0.5m, DisplayStyle.Fraction));
    }

    // ── Simplify: tie-breaks (§4/§6) ───────────────────────────────────────────────
    // Natural score ties do not arise in the seeded spoon family (unit changes cost +1 and the
    // fraction penalties differ), so the two tie-break rules — authored unit first, then larger
    // unit — are exercised with synthetic integer-ratio units built purely to force an equal score.

    [Fact]
    public void Simplify_ScoreTie_KeepsAuthoredUnit()
    {
        // A (authored, factor 1) and B (factor 2), both Fraction, integer ratio. At amount 2:
        //   A rep "2"  → 2 + 0 + 0(authored) = 2
        //   B rep "1"  → 1 + 0 + 1(changed)  = 2   ← tie
        // Tie resolves to the authored unit A.
        var a = MakeUnit("a", Dimension.Volume, 1m, DisplayStyle.Fraction, UnitSystem.UsCustomary, isBase: true);
        var b = MakeUnit("b", Dimension.Volume, 2m, DisplayStyle.Fraction, UnitSystem.UsCustomary);
        IReadOnlyList<CatalogUnit> synthetic = [a, b];

        var (_, unitId) = QuantityDisplay.Simplify(2m, a.Id.Value, synthetic);

        Assert.Equal(a.Id.Value, unitId);
    }

    [Fact]
    public void Simplify_ScoreTie_BetweenSiblings_PrefersLargerUnit()
    {
        // A (authored, factor 1) never wins at amount 8 (its rep "8" scores 8). Two non-authored
        // siblings tie:
        //   B (factor 8)  rep "1"  → 1 + 0 + 1 = 2
        //   C (factor 16) rep "½"  → 0 + 1 + 1 = 2   ← tie with B
        // Tie resolves to the larger unit → C.
        var a = MakeUnit("a", Dimension.Volume, 1m,  DisplayStyle.Fraction, UnitSystem.UsCustomary, isBase: true);
        var b = MakeUnit("b", Dimension.Volume, 8m,  DisplayStyle.Fraction, UnitSystem.UsCustomary);
        var c = MakeUnit("c", Dimension.Volume, 16m, DisplayStyle.Fraction, UnitSystem.UsCustomary);
        IReadOnlyList<CatalogUnit> synthetic = [a, b, c];

        var (amount, unitId) = QuantityDisplay.Simplify(8m, a.Id.Value, synthetic);

        Assert.Equal(c.Id.Value, unitId);
        Assert.Equal(0.5m, amount);
    }

    [Fact]
    public void Simplify_PrefersFewerScoops_QuarterCup_Over_4Tbsp()
    {
        var (amount, unitId) = QuantityDisplay.Simplify(4m, Tbsp.Id.Value, Units);
        Assert.Equal(Cup.Id.Value, unitId);
        Assert.Equal(0.25m, amount);
    }

    // ── Simplify: integer-ratio firewall (§5 exclusion, Q5) ────────────────────────

    [Fact]
    public void Simplify_NeverProposes_MetricUnit_ForSpoonAmount()
    {
        // Authored tbsp (UsCustomary) with ml (Metric) present: the UnitSystem firewall must never
        // propose ml, at any scale.
        foreach (var scale in new[] { 0.5m, 1.5m, 2m, 3m, 7m })
        {
            var (_, unitId) = QuantityDisplay.Simplify(2m * scale, Tbsp.Id.Value, Units);
            Assert.NotEqual(Ml.Id.Value, unitId);
        }
    }

    [Fact]
    public void Simplify_NeverProposes_MetricUnit_EvenAtWholeNumberRatio()
    {
        // The seed-factor gap the UnitSystem tag closes: an imperial→metric rewrite with an EXACT
        // whole-number ratio. Authored tbsp = 15, ml = 1 (both Fraction-styled here so only the system
        // tag differs), 15 ml/tbsp is a whole ratio — the old integer-ratio-only firewall would have let
        // "1 tbsp × 2 = 30 ml" through. The system tag blocks it: tbsp keeps its family, never ml.
        var tbsp = MakeUnit("tbsp", Dimension.Volume, 15m, DisplayStyle.Fraction, UnitSystem.UsCustomary);
        var mlF  = MakeUnit("ml",   Dimension.Volume, 1m,  DisplayStyle.Fraction, UnitSystem.Metric, isBase: true);
        IReadOnlyList<CatalogUnit> family = [tbsp, mlF];

        foreach (var scale in new[] { 2m, 3m, 8m })
        {
            var (_, unitId) = QuantityDisplay.Simplify(1m * scale, tbsp.Id.Value, family);
            Assert.NotEqual(mlF.Id.Value, unitId);
        }
    }

    [Fact]
    public void Simplify_MetricAuthored_NeverProposesImperial_EvenAtWholeNumberRatio()
    {
        // The reverse breach (metric→imperial) the old firewall allowed live: authored 480 ml with a
        // Fraction-styled cup = 240 present. 480 / 240 = 2 is an exact whole ratio, so integer-ratio
        // alone would rewrite "480 ml" → "2 cup". The system tag (ml Metric ≠ cup UsCustomary) keeps it
        // 480 ml — and because ml's own rep snaps to whole "480", the authored unit is returned unchanged.
        var mlF = MakeUnit("ml",  Dimension.Volume, 1m,   DisplayStyle.Fraction, UnitSystem.Metric, isBase: true);
        var cup = MakeUnit("cup", Dimension.Volume, 240m, DisplayStyle.Fraction, UnitSystem.UsCustomary);
        IReadOnlyList<CatalogUnit> family = [mlF, cup];

        var (amount, unitId) = QuantityDisplay.Simplify(480m, mlF.Id.Value, family);

        Assert.Equal(mlF.Id.Value, unitId);
        Assert.Equal(480m, amount);
    }

    [Fact]
    public void Simplify_UnspecifiedAuthored_AnchorsNoFamily_ReturnsInputUnchanged()
    {
        // An authored unit with Unspecified system anchors no simplification family: even with a
        // same-dimension, Fraction-styled, whole-ratio sibling present, nothing is proposed.
        var custom  = MakeUnit("scoop", Dimension.Volume, 60m, DisplayStyle.Fraction, UnitSystem.Unspecified);
        var cup     = MakeUnit("cup",   Dimension.Volume, 240m, DisplayStyle.Fraction, UnitSystem.UsCustomary); // 4 × scoop
        IReadOnlyList<CatalogUnit> family = [custom, cup];

        var (amount, unitId) = QuantityDisplay.Simplify(4m, custom.Id.Value, family);

        Assert.Equal(custom.Id.Value, unitId);
        Assert.Equal(4m, amount);
    }

    [Fact]
    public void Simplify_NeverProposes_FractionSibling_WithNonIntegerRatio()
    {
        // A Fraction-styled, UsCustomary dessertspoon at 10 ml sits at a non-integer ratio to tbsp
        // (14.78676 / 10 = 1.478…). Even though it shares the system AND opts into fractions, the
        // integer-ratio math guarantee (not merely the system/DisplayStyle filters) must exclude it.
        var dsp = MakeUnit("dsp", Dimension.Volume, 10m, DisplayStyle.Fraction, UnitSystem.UsCustomary);
        IReadOnlyList<CatalogUnit> withDsp = [Ml, Tsp, Tbsp, Cup, Gram, dsp];

        var (_, unitId) = QuantityDisplay.Simplify(4m, Tbsp.Id.Value, withDsp);

        Assert.NotEqual(dsp.Id.Value, unitId);
        Assert.Equal(Cup.Id.Value, unitId); // still simplifies to ¼ cup as the golden table requires
    }

    // ── Simplify: Decimal-styled authored unit passes through unchanged ────────────

    [Fact]
    public void Simplify_DecimalAuthoredUnit_ReturnsInputUnit()
    {
        var (amount, unitId) = QuantityDisplay.Simplify(500m, Ml.Id.Value, Units);
        Assert.Equal(Ml.Id.Value, unitId);
        Assert.Equal(500m, amount);
    }

    [Fact]
    public void Simplify_NoSnappingCandidate_ReturnsInputUnchanged()
    {
        // 0.55 cup snaps in no unit (cup 0.55, tbsp 8.8, tsp 26.4 all miss) → input unchanged (Q7).
        var (amount, unitId) = QuantityDisplay.Simplify(0.55m, Cup.Id.Value, Units);
        Assert.Equal(Cup.Id.Value, unitId);
        Assert.Equal(0.55m, amount);
    }

    [Fact]
    public void Simplify_UnknownUnit_ReturnsInputUnchanged()
    {
        var strayId = Guid.NewGuid();
        var (amount, unitId) = QuantityDisplay.Simplify(3m, strayId, Units);
        Assert.Equal(strayId, unitId);
        Assert.Equal(3m, amount);
    }
}
