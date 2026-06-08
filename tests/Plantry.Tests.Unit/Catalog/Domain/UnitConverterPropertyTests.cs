using CsCheck;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

/// <summary>
/// L1b property-based tests (CsCheck) for <see cref="UnitConverter"/> — the gnarly invariants
/// that must hold for *all* inputs (PHASE-1-PLAN.md "the math that must hold"):
/// within-dimension round-trips, factor-ratio determinism, product-conversion inverse
/// symmetry, and fail-loud for any cross-dimension pair lacking a <see cref="ProductConversion"/>.
/// </summary>
public sealed class UnitConverterPropertyTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();

    // Bounded ranges keep decimal arithmetic comfortably away from overflow/underflow while
    // still exercising a wide spread of magnitudes.
    private static readonly Gen<decimal> GenAmount = Gen.Decimal[0.01m, 100_000m];
    private static readonly Gen<decimal> GenFactor = Gen.Decimal[0.01m, 10_000m];

    private static readonly Gen<(decimal amount, decimal factorA, decimal factorB)> GenRoundTripInputs =
        Gen.Select(GenAmount, GenFactor, GenFactor);

    private static bool ApproximatelyEqual(decimal expected, decimal actual)
    {
        var tolerance = Math.Abs(expected) * 0.0000000001m; // 1e-10 relative — clears decimal rounding noise
        return Math.Abs(expected - actual) <= tolerance;
    }

    [Fact(DisplayName = "Within-dimension round-trip a→b→a ≈ a for any positive factors")]
    public void WithinDimension_RoundTrip_Approximately_Returns_Original()
    {
        GenRoundTripInputs.Sample(t =>
        {
            var (amount, factorA, factorB) = t;
            var unitA = CatalogUnit.Create(HouseholdId, "a", "a", Dimension.Mass, factorA);
            var unitB = CatalogUnit.Create(HouseholdId, "b", "b", Dimension.Mass, factorB);
            var units = new[] { unitA, unitB };

            var toB = UnitConverter.Convert(amount, unitA.Id.Value, unitB.Id.Value, units, []);
            Assert.True(toB.IsSuccess);

            var backToA = UnitConverter.Convert(toB.Value, unitB.Id.Value, unitA.Id.Value, units, []);
            Assert.True(backToA.IsSuccess);

            Assert.True(ApproximatelyEqual(amount, backToA.Value),
                $"amount={amount}, factorA={factorA}, factorB={factorB}: {amount} -> {toB.Value} -> {backToA.Value}");
        });
    }

    [Fact(DisplayName = "Within-dimension conversion is exactly amount × (factorFrom / factorTo)")]
    public void WithinDimension_Conversion_Matches_FactorRatio_Deterministically()
    {
        GenRoundTripInputs.Sample(t =>
        {
            var (amount, factorA, factorB) = t;
            var unitA = CatalogUnit.Create(HouseholdId, "a", "a", Dimension.Volume, factorA);
            var unitB = CatalogUnit.Create(HouseholdId, "b", "b", Dimension.Volume, factorB);
            var units = new[] { unitA, unitB };

            var expected = amount * (factorA / factorB);
            var result = UnitConverter.Convert(amount, unitA.Id.Value, unitB.Id.Value, units, []);

            Assert.True(result.IsSuccess);
            Assert.Equal(expected, result.Value);
        });
    }

    [Fact(DisplayName = "Product-conversion inverse is symmetric: a→b via factor, b→a via 1/factor")]
    public void ProductConversion_Inverse_Is_Symmetric_With_Direct()
    {
        Gen.Select(GenAmount, GenFactor).Sample(t =>
        {
            var (amount, factor) = t;
            var unitA = CatalogUnit.Create(HouseholdId, "cup", "cup", Dimension.Volume, 1m);
            var unitB = CatalogUnit.Create(HouseholdId, "g", "g", Dimension.Mass, 1m);
            var units = new[] { unitA, unitB };

            var owner = Product.Create(HouseholdId, "Flour", unitB.Id, SystemClock.Instance);
            var conversion = owner.AddConversion(unitA.Id, unitB.Id, factor, SystemClock.Instance);
            var conversions = new[] { conversion };

            var direct = UnitConverter.Convert(amount, unitA.Id.Value, unitB.Id.Value, units, conversions);
            Assert.True(direct.IsSuccess);
            Assert.Equal(amount * factor, direct.Value);

            var inverse = UnitConverter.Convert(direct.Value, unitB.Id.Value, unitA.Id.Value, units, conversions);
            Assert.True(inverse.IsSuccess);

            Assert.True(ApproximatelyEqual(amount, inverse.Value),
                $"amount={amount}, factor={factor}: {amount} -> {direct.Value} -> {inverse.Value}");
        });
    }

    [Fact(DisplayName = "Fail-loud: any cross-dimension pair lacking a ProductConversion is unresolvable")]
    public void CrossDimension_Without_ProductConversion_AlwaysFailsLoudly()
    {
        Gen.Select(GenFactor, GenFactor, GenAmount).Sample(t =>
        {
            var (factorA, factorB, amount) = t;
            var unitA = CatalogUnit.Create(HouseholdId, "a", "a", Dimension.Mass, factorA);
            var unitB = CatalogUnit.Create(HouseholdId, "b", "b", Dimension.Volume, factorB);
            var units = new[] { unitA, unitB };

            var result = UnitConverter.Convert(amount, unitA.Id.Value, unitB.Id.Value, units, []);

            Assert.True(result.IsFailure);
            Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
        });
    }
}
