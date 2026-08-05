using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

public sealed class SubstitutionTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid TargetProduct = Guid.NewGuid();
    private static readonly Guid TargetUnit = Guid.NewGuid();
    private static readonly Guid SubstituteProduct = Guid.NewGuid();
    private static readonly Guid SubstituteUnit = Guid.NewGuid();

    [Fact(DisplayName = "Create sets every field and stamps CreatedAt/UpdatedAt")]
    public void Create_Sets_Fields()
    {
        // A fixed instant, not SystemClock.Instance: Create reads clock.UtcNow twice (CreatedAt then
        // UpdatedAt) and the real wall clock can tick between those two reads, making the "same instant"
        // assertion below flaky.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var substitution = Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            SubstituteProduct, 154m, SubstituteUnit,
            new FixedClock(now));

        Assert.Equal(Household, substitution.HouseholdId);
        Assert.Equal(TargetProduct, substitution.TargetProductId);
        Assert.Equal(400m, substitution.TargetQuantity);
        Assert.Equal(TargetUnit, substitution.TargetUnitId);
        Assert.Equal(SubstituteProduct, substitution.SubstituteProductId);
        Assert.Equal(154m, substitution.SubstituteQuantity);
        Assert.Equal(SubstituteUnit, substitution.SubstituteUnitId);
        Assert.Equal(now, substitution.CreatedAt);
        Assert.Equal(now, substitution.UpdatedAt);
    }

    [Theory(DisplayName = "Create rejects empty (Guid.Empty) product/unit ids on either side")]
    [InlineData(true, false, false, false)]  // empty target product
    [InlineData(false, true, false, false)]  // empty target unit
    [InlineData(false, false, true, false)]  // empty substitute product
    [InlineData(false, false, false, true)]  // empty substitute unit
    public void Create_Rejects_Empty_Ids(
        bool emptyTargetProduct, bool emptyTargetUnit, bool emptySubstituteProduct, bool emptySubstituteUnit)
    {
        var targetProduct = emptyTargetProduct ? Guid.Empty : TargetProduct;
        var targetUnit = emptyTargetUnit ? Guid.Empty : TargetUnit;
        var substituteProduct = emptySubstituteProduct ? Guid.Empty : SubstituteProduct;
        var substituteUnit = emptySubstituteUnit ? Guid.Empty : SubstituteUnit;

        Assert.Throws<ArgumentException>(() => Substitution.Create(
            Household,
            targetProduct, 400m, targetUnit,
            substituteProduct, 154m, substituteUnit,
            Clock));
    }

    [Fact(DisplayName = "Create rejects self-substitution (substitute == target)")]
    public void Create_Rejects_SelfSubstitution()
    {
        Assert.Throws<ArgumentException>(() => Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            TargetProduct, 154m, SubstituteUnit,
            Clock));
    }

    [Theory(DisplayName = "Create rejects non-positive target quantity")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_Rejects_NonPositive_TargetQuantity(decimal quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Substitution.Create(
            Household,
            TargetProduct, quantity, TargetUnit,
            SubstituteProduct, 154m, SubstituteUnit,
            Clock));
    }

    [Theory(DisplayName = "Create rejects non-positive substitute quantity")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_Rejects_NonPositive_SubstituteQuantity(decimal quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            SubstituteProduct, quantity, SubstituteUnit,
            Clock));
    }

    [Fact(DisplayName = "ReplaceRatio updates quantities/units and bumps UpdatedAt, leaving CreatedAt untouched")]
    public void ReplaceRatio_Updates_Ratio_And_UpdatedAt()
    {
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = earlier.AddDays(1);
        var newTargetUnit = Guid.NewGuid();
        var newSubstituteUnit = Guid.NewGuid();
        var substitution = Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            SubstituteProduct, 154m, SubstituteUnit,
            new FixedClock(earlier));

        substitution.ReplaceRatio(500m, newTargetUnit, 200m, newSubstituteUnit, new FixedClock(later));

        Assert.Equal(500m, substitution.TargetQuantity);
        Assert.Equal(newTargetUnit, substitution.TargetUnitId);
        Assert.Equal(200m, substitution.SubstituteQuantity);
        Assert.Equal(newSubstituteUnit, substitution.SubstituteUnitId);
        Assert.Equal(later, substitution.UpdatedAt);
        Assert.Equal(earlier, substitution.CreatedAt);
    }

    [Fact(DisplayName = "ReplaceRatio rejects an empty unit id and leaves the existing ratio untouched")]
    public void ReplaceRatio_Rejects_Empty_Unit_Id()
    {
        var substitution = Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            SubstituteProduct, 154m, SubstituteUnit,
            Clock);

        Assert.Throws<ArgumentException>(() =>
            substitution.ReplaceRatio(500m, Guid.Empty, 200m, SubstituteUnit, Clock));

        Assert.Equal(TargetUnit, substitution.TargetUnitId);
        Assert.Equal(400m, substitution.TargetQuantity);
    }

    [Fact(DisplayName = "ReplaceRatio rejects non-positive quantities and leaves the existing ratio untouched")]
    public void ReplaceRatio_Rejects_NonPositive_Quantities()
    {
        var substitution = Substitution.Create(
            Household,
            TargetProduct, 400m, TargetUnit,
            SubstituteProduct, 154m, SubstituteUnit,
            Clock);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            substitution.ReplaceRatio(0m, TargetUnit, 154m, SubstituteUnit, Clock));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            substitution.ReplaceRatio(400m, TargetUnit, -1m, SubstituteUnit, Clock));

        Assert.Equal(400m, substitution.TargetQuantity);
        Assert.Equal(154m, substitution.SubstituteQuantity);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public TimeZoneInfo Zone { get; } = TimeZoneInfo.Utc;
    }
}
