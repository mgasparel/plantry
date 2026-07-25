using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

/// <summary>
/// L1 unit tests for <see cref="UnitConverter"/> — the heavily-tested conversion-resolution
/// core (DM-12 / PHASE-1-PLAN.md "the math that must hold"). Exercises the resolution order
/// exactly as documented: same unit → same dimension → product conversion (incl. inverse) →
/// fail loudly.
/// </summary>
public sealed class UnitConverterTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly ProductId ProductId = Plantry.Catalog.Domain.ProductId.New();

    private static CatalogUnit MakeUnit(string code, Dimension dimension, decimal factorToBase, bool isBase = false) =>
        CatalogUnit.Create(HouseholdId, code, code, dimension, factorToBase, isBase);

    private static ProductConversion MakeConversion(Product owner, UnitId from, UnitId to, decimal factor) =>
        owner.AddConversion(from, to, factor, SystemClock.Instance);

    [Fact]
    public void SameUnit_Resolves_To_Identity_Regardless_Of_KnownUnits()
    {
        var unitId = Plantry.Catalog.Domain.UnitId.New();

        var result = UnitConverter.Convert(5m, unitId.Value, unitId.Value, [], []);

        Assert.True(result.IsSuccess);
        Assert.Equal(5m, result.Value);
    }

    [Fact]
    public void SameDimension_Scales_Linearly_Via_FactorToBase()
    {
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var kilograms = MakeUnit("kg", Dimension.Mass, 1000m);

        var result = UnitConverter.Convert(2m, kilograms.Id.Value, grams.Id.Value, [grams, kilograms], []);

        Assert.True(result.IsSuccess);
        Assert.Equal(2000m, result.Value);
    }

    [Fact]
    public void SameDimension_Conversion_Is_Symmetric_With_Its_Inverse()
    {
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var kilograms = MakeUnit("kg", Dimension.Mass, 1000m);

        var forward = UnitConverter.Convert(2m, kilograms.Id.Value, grams.Id.Value, [grams, kilograms], []);
        var backward = UnitConverter.Convert(forward.Value, grams.Id.Value, kilograms.Id.Value, [grams, kilograms], []);

        Assert.True(forward.IsSuccess);
        Assert.True(backward.IsSuccess);
        Assert.Equal(2m, backward.Value);
    }

    [Fact]
    public void ProductConversion_DirectDirection_Scales_By_Factor()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        var conversion = MakeConversion(product, cups.Id, grams.Id, 120m);

        var result = UnitConverter.Convert(2m, cups.Id.Value, grams.Id.Value, [cups, grams], [conversion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(240m, result.Value);
    }

    /// <summary>
    /// plantry-qno9 AC1: an author who states the fact against ANY unit pair — "1 kg = 8 cups", stored
    /// as (kg → cup, 8) with NEITHER anchor being the product default (g) — still resolves a recipe
    /// measured in cups against stock in g. The converter bridges the stock side (kg → g, ×1000) and
    /// inverts the conversion, so 2 cup → 250 g. Regression cover for the "define against any unit" flow.
    /// </summary>
    [Fact]
    public void ProductConversion_AnchoredOnNonDefaultUnits_ResolvesRecipeUnitAgainstStock()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var kilograms = MakeUnit("kg", Dimension.Mass, 1000m);
        var product = Product.Create(HouseholdId, "Cashews", grams.Id, SystemClock.Instance);
        // The author's verbatim fact: "1 kg = 8 cups" → (from = kg, to = cup, factor = 8).
        var conversion = MakeConversion(product, kilograms.Id, cups.Id, 8m);

        var result = UnitConverter.Convert(
            2m, cups.Id.Value, grams.Id.Value, [cups, grams, kilograms], [conversion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(250m, result.Value);
    }

    [Fact]
    public void ProductConversion_InverseDirection_Divides_By_Factor()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        // Stored as "1 cup = 120 g"; resolving g → cup must use the inverse.
        var conversion = MakeConversion(product, cups.Id, grams.Id, 120m);

        var result = UnitConverter.Convert(120m, grams.Id.Value, cups.Id.Value, [cups, grams], [conversion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(1m, result.Value);
    }

    [Fact]
    public void ProductConversion_Inverse_RoundTrips_With_Direct()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        var conversion = MakeConversion(product, cups.Id, grams.Id, 120m);

        var toGrams = UnitConverter.Convert(3m, cups.Id.Value, grams.Id.Value, [cups, grams], [conversion]);
        var backToCups = UnitConverter.Convert(toGrams.Value, grams.Id.Value, cups.Id.Value, [cups, grams], [conversion]);

        Assert.True(toGrams.IsSuccess);
        Assert.True(backToCups.IsSuccess);
        Assert.Equal(3m, backToCups.Value);
    }

    [Fact]
    public void CrossDimension_Without_ProductConversion_FailsLoudly()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);

        var result = UnitConverter.Convert(2m, cups.Id.Value, grams.Id.Value, [cups, grams], []);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
    }

    [Fact]
    public void UnknownUnits_Without_ProductConversion_FailsLoudly()
    {
        var fromId = Plantry.Catalog.Domain.UnitId.New();
        var toId = Plantry.Catalog.Domain.UnitId.New();

        var result = UnitConverter.Convert(1m, fromId.Value, toId.Value, [], []);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
    }

    [Fact]
    public void Never_Returns_Identity_Or_Zero_For_An_Unresolvable_Pair()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);

        var result = UnitConverter.Convert(5m, cups.Id.Value, grams.Id.Value, [cups, grams], []);

        // Fail-loud means a Result.Failure, not a silently-wrong success value.
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ChainedConversion_BridgesSameDimensionHop_Then_ProductConversion()
    {
        // Product is stocked in kg with only one conversion on file: "1 kg = 8 cups".
        // A recipe asks for 2 tbsp — resolving tbsp -> kg requires bridging the
        // same-dimension hop (tbsp -> cup, both Volume) and then applying the
        // product conversion's inverse (cup -> kg).
        var tablespoons = MakeUnit("tbsp", Dimension.Volume, 15m);
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var kilograms = MakeUnit("kg", Dimension.Mass, 1000m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Flour", kilograms.Id, SystemClock.Instance);
        var conversion = MakeConversion(product, kilograms.Id, cups.Id, 8m);

        var result = UnitConverter.Convert(
            2m, tablespoons.Id.Value, kilograms.Id.Value,
            [tablespoons, cups, kilograms, grams], [conversion]);

        // 2 tbsp = 2 * (15 / 240) cup = 0.125 cup; 1 kg = 8 cup, so 0.125 cup = 0.125 / 8 kg.
        Assert.True(result.IsSuccess);
        Assert.Equal(0.015625m, result.Value);
    }

    [Fact]
    public void AiSuggested_Conversion_Resolves_Like_Any_Other()
    {
        // ADR-022: provenance is metadata, not a computation gate. An ai_suggested lb→each
        // bridge must resolve exactly as a user-confirmed one would.
        var pounds = MakeUnit("lb", Dimension.Mass, 453.592m);
        var each = MakeUnit("each", Dimension.Count, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Bananas", each.Id, SystemClock.Instance);
        var suggested = product.AddConversion(pounds.Id, each.Id, 5m, SystemClock.Instance, ConversionSource.AiSuggested);

        Assert.True(suggested.IsAiSuggested); // guard: the bridge really is a suggestion

        var forward = UnitConverter.Convert(2m, pounds.Id.Value, each.Id.Value, [pounds, each], [suggested]);
        var inverse = UnitConverter.Convert(10m, each.Id.Value, pounds.Id.Value, [pounds, each], [suggested]);

        Assert.True(forward.IsSuccess);
        Assert.Equal(10m, forward.Value); // 2 lb × 5 each/lb
        Assert.True(inverse.IsSuccess);
        Assert.Equal(2m, inverse.Value); // 10 each ÷ 5 each/lb
    }

    [Fact]
    public void DirectProductConversion_PreferredOver_Inverse_When_Both_Match()
    {
        // Pathological double-entry data: both directions stored. Direct must win, by
        // resolution-order contract, so behaviour stays deterministic.
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        var direct = MakeConversion(product, cups.Id, grams.Id, 120m);
        var alsoDirect = product.AddConversion(grams.Id, cups.Id, 1m / 100m, SystemClock.Instance);

        var result = UnitConverter.Convert(1m, cups.Id.Value, grams.Id.Value, [cups, grams], [direct, alsoDirect]);

        Assert.True(result.IsSuccess);
        Assert.Equal(120m, result.Value);
    }

    // ── plantry-xddq: multi-hop graph-walk composition ─────────────────────────

    /// <summary>
    /// plantry-xddq bug-report shape exactly: product 019f9a21-... has cup=105g, srv=0.75cup,
    /// pk=600g on file — no direct srv↔pk conversion. Converting srv → pk must chain THREE
    /// edges (srv --same-dimension--> cup is wrong; srv/cup are both Count/Volume respectively
    /// via the conversion, so the real path is srv --conv--> cup --conv--> g --conv(inverse)--> pk)
    /// rather than colliding on the old same-dimension Count fast path.
    /// </summary>
    [Fact]
    public void MultiHopProductConversions_Compose_Through_SharedPivotUnit()
    {
        var cups = MakeUnit("cup", Dimension.Volume, 240m);
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);
        var servings = MakeUnit("srv", Dimension.Count, 1m, isBase: true);
        var packs = MakeUnit("pk", Dimension.Count, 1m, isBase: true);
        var product = Product.Create(HouseholdId, "Oat Bar", servings.Id, SystemClock.Instance);
        var cupToGrams = MakeConversion(product, cups.Id, grams.Id, 105m);       // 1 cup = 105 g
        var srvToCup = MakeConversion(product, servings.Id, cups.Id, 0.75m);     // 1 srv = 0.75 cup
        var pkToGrams = MakeConversion(product, packs.Id, grams.Id, 600m);       // 1 pk = 600 g
        var allUnits = new[] { cups, grams, servings, packs };
        var allConversions = new[] { cupToGrams, srvToCup, pkToGrams };

        // 1 srv = 0.75 cup = 0.75 * 105 g = 78.75 g; 78.75 g / 600 g/pk = 0.13125 pk.
        var srvToPk = UnitConverter.Convert(1m, servings.Id.Value, packs.Id.Value, allUnits, allConversions);
        Assert.True(srvToPk.IsSuccess);
        Assert.Equal(0.13125m, srvToPk.Value);

        // g -> srv: 105 g -> 1 cup -> 1/0.75 srv = 1.3333... srv.
        var gToSrv = UnitConverter.Convert(105m, grams.Id.Value, servings.Id.Value, allUnits, allConversions);
        Assert.True(gToSrv.IsSuccess);
        Assert.Equal(1m / 0.75m, gToSrv.Value);

        // pk -> srv (both directions of the 3-hop chain): 1 pk = 600 g = 600/105 cup = /0.75 srv.
        var pkToSrv = UnitConverter.Convert(1m, packs.Id.Value, servings.Id.Value, allUnits, allConversions);
        Assert.True(pkToSrv.IsSuccess);
        Assert.Equal(600m / 105m / 0.75m, pkToSrv.Value);
    }

    /// <summary>
    /// Regression for the exact collision this bug report root-caused: two DIFFERENT
    /// Dimension.Count units on the same product, with NO ProductConversion linking them, must
    /// fail loudly — not silently return fromUnit.FactorToBase / toUnit.FactorToBase (which
    /// defaults to 1/1 = 1 for freshly-created units, i.e. a bogus "1:1" conversion).
    /// </summary>
    [Fact]
    public void TwoDistinctCountUnits_WithNoConnectingConversion_FailsLoudly()
    {
        var servings = MakeUnit("srv", Dimension.Count, 1m, isBase: true);
        var packs = MakeUnit("pk", Dimension.Count, 1m);

        var result = UnitConverter.Convert(1m, servings.Id.Value, packs.Id.Value, [servings, packs], []);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
    }

    /// <summary>
    /// Same collision, but with non-trivial FactorToBase values on the Count units, to prove the
    /// fail-loud path isn't merely an artifact of both units defaulting to factor 1.
    /// </summary>
    [Fact]
    public void TwoDistinctCountUnits_WithDifferentFactorToBase_StillFailLoudly_NotByCoincidence()
    {
        var dozens = MakeUnit("dz", Dimension.Count, 12m);
        var each = MakeUnit("each", Dimension.Count, 1m, isBase: true);
        var cases = MakeUnit("case", Dimension.Count, 24m);

        var result = UnitConverter.Convert(1m, dozens.Id.Value, cases.Id.Value, [dozens, each, cases], []);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
    }

    /// <summary>
    /// A Count unit remains freely convertible with itself (identity short-circuit) even though
    /// Count no longer gets a free same-dimension edge to OTHER Count units.
    /// </summary>
    [Fact]
    public void SameCountUnit_StillResolvesToIdentity()
    {
        var servings = MakeUnit("srv", Dimension.Count, 1m, isBase: true);

        var result = UnitConverter.Convert(3m, servings.Id.Value, servings.Id.Value, [servings], []);

        Assert.True(result.IsSuccess);
        Assert.Equal(3m, result.Value);
    }

    /// <summary>
    /// A Count unit CAN still reach another Count unit when a ProductConversion explicitly
    /// connects them directly (not merely via a shared-dimension freebie).
    /// </summary>
    [Fact]
    public void TwoDistinctCountUnits_WithDirectProductConversion_Resolve()
    {
        var servings = MakeUnit("srv", Dimension.Count, 1m, isBase: true);
        var packs = MakeUnit("pk", Dimension.Count, 1m);
        var product = Product.Create(HouseholdId, "Snack Bar", servings.Id, SystemClock.Instance);
        var conversion = MakeConversion(product, packs.Id, servings.Id, 6m); // 1 pk = 6 srv

        var result = UnitConverter.Convert(2m, packs.Id.Value, servings.Id.Value, [servings, packs], [conversion]);

        Assert.True(result.IsSuccess);
        Assert.Equal(12m, result.Value);
    }
}

/// <summary>
/// L2 unit tests for <see cref="UnitConverter.ReachableUnits"/> — the C10 unit-selector
/// derivation helper that enumerates every unit a product can be counted in.
/// </summary>
public sealed class UnitConverterReachableUnitsTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();

    private static Plantry.Catalog.Domain.Unit MakeUnit(
        string code, Dimension dimension, decimal factorToBase, bool isBase = false) =>
        Plantry.Catalog.Domain.Unit.Create(HouseholdId, code, code, dimension, factorToBase, isBase);

    private static ProductConversion MakeConversion(Product owner, UnitId from, UnitId to, decimal factor) =>
        owner.AddConversion(from, to, factor, SystemClock.Instance);

    // ── No-conversion case ─────────────────────────────────────────────────────

    [Fact]
    public void NoConversions_Returns_SingleElement_DefaultOnly()
    {
        var grams = MakeUnit("g", Dimension.Mass, 1m, isBase: true);

        var result = UnitConverter.ReachableUnits(grams.Id.Value, [grams], []);

        Assert.Equal([grams.Id.Value], result);
    }

    [Fact]
    public void NoConversions_DefaultUnitNotInAllUnits_Returns_SingleElement()
    {
        // Edge case: default unit id not in allUnits list (should still appear).
        var orphanId = Guid.NewGuid();

        var result = UnitConverter.ReachableUnits(orphanId, [], []);

        Assert.Equal([orphanId], result);
    }

    // ── Same-dimension case ────────────────────────────────────────────────────

    [Fact]
    public void SameDimension_IncludesAllSiblings_NoPConversionNeeded()
    {
        var grams       = MakeUnit("g",  Dimension.Mass, 1m,    isBase: true);
        var kilograms   = MakeUnit("kg", Dimension.Mass, 1000m);
        var milligrams  = MakeUnit("mg", Dimension.Mass, 0.001m);
        var cups        = MakeUnit("cup", Dimension.Volume, 240m);

        // Default is g — its dimension (Mass) makes g, kg, mg all reachable; cup is not.
        var result = UnitConverter.ReachableUnits(grams.Id.Value, [grams, kilograms, milligrams, cups], []);

        Assert.Contains(grams.Id.Value, result);
        Assert.Contains(kilograms.Id.Value, result);
        Assert.Contains(milligrams.Id.Value, result);
        Assert.DoesNotContain(cups.Id.Value, result);
    }

    [Fact]
    public void SameDimension_DefaultIsFirst()
    {
        var grams       = MakeUnit("g",  Dimension.Mass, 1m,    isBase: true);
        var kilograms   = MakeUnit("kg", Dimension.Mass, 1000m);
        var milligrams  = MakeUnit("mg", Dimension.Mass, 0.001m);

        var result = UnitConverter.ReachableUnits(kilograms.Id.Value, [grams, kilograms, milligrams], []);

        Assert.Equal(kilograms.Id.Value, result[0]);
    }

    // ── ProductConversion-bridged case ─────────────────────────────────────────

    [Fact]
    public void ProductConversion_AddsFromAndToUnits_AndTheirSameDimensionSiblings()
    {
        // Product: default unit = g (Mass), conversion: 1 cup = 120 g.
        // Reachable: g, kg (Mass siblings), cup, tablespoon (Volume siblings via cup anchor).
        var grams      = MakeUnit("g",    Dimension.Mass,   1m,    isBase: true);
        var kilograms  = MakeUnit("kg",   Dimension.Mass,   1000m);
        var cups       = MakeUnit("cup",  Dimension.Volume, 240m);
        var tablespoon = MakeUnit("tbsp", Dimension.Volume, 15m);

        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        var conv = MakeConversion(product, cups.Id, grams.Id, 120m);

        var result = UnitConverter.ReachableUnits(
            grams.Id.Value,
            [grams, kilograms, cups, tablespoon],
            [conv]);

        Assert.Contains(grams.Id.Value,      result);
        Assert.Contains(kilograms.Id.Value,  result);
        Assert.Contains(cups.Id.Value,       result);
        Assert.Contains(tablespoon.Id.Value, result);
    }

    [Fact]
    public void ProductConversion_DefaultFirst_ThenAlphaByCode()
    {
        var grams      = MakeUnit("g",   Dimension.Mass,   1m,    isBase: true);
        var kilograms  = MakeUnit("kg",  Dimension.Mass,   1000m);
        var cups       = MakeUnit("cup", Dimension.Volume, 240m);

        var product = Product.Create(HouseholdId, "Flour", grams.Id, SystemClock.Instance);
        var conv = MakeConversion(product, cups.Id, grams.Id, 120m);

        var result = UnitConverter.ReachableUnits(
            grams.Id.Value,
            [grams, kilograms, cups],
            [conv]);

        // g must be first (it is the default); remaining should be alphabetically cup, kg.
        Assert.Equal(grams.Id.Value, result[0]);
        Assert.Equal(cups.Id.Value,  result[1]); // 'c' < 'k'
        Assert.Equal(kilograms.Id.Value, result[2]);
    }

    // ── Default-first ordering ─────────────────────────────────────────────────

    [Fact]
    public void DefaultUnit_IsAlwaysFirst_EvenIfCodeComesLaterAlphabetically()
    {
        var grams     = MakeUnit("g",  Dimension.Mass, 1m, isBase: true);
        var kilograms = MakeUnit("kg", Dimension.Mass, 1000m);

        // Default is kg — code 'k' would sort after 'g', but kg must still come first.
        var result = UnitConverter.ReachableUnits(kilograms.Id.Value, [grams, kilograms], []);

        Assert.Equal(kilograms.Id.Value, result[0]);
        Assert.Equal(grams.Id.Value,     result[1]);
    }
}
