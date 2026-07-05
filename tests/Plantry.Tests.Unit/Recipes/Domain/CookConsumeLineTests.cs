using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

/// <summary>
/// L1 domain tests for <see cref="CookConsumeLine"/> lifecycle mutators and the
/// <see cref="CookConsumeLineStatus"/> text mapping — focused on the plantry-qll2.6 additions
/// (<see cref="CookConsumeLineStatus.DeferredUnitGap"/> / <see cref="CookConsumeLineStatus.SupersededByCount"/>).
/// Lines are created via <see cref="CookEvent.AddConsumeLine"/> (the internal ctor is not public).
/// </summary>
public sealed class CookConsumeLineTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    private static CookConsumeLine NewLine(decimal quantity = 100m)
    {
        var cookEvent = CookEvent.Record(
            RecipeId.New(), HouseholdId.New(), servingsCooked: 2, Guid.CreateVersion7(), Clock).Value;
        return cookEvent.AddConsumeLine(Guid.CreateVersion7(), Guid.CreateVersion7(), quantity, Guid.CreateVersion7());
    }

    [Fact]
    public void New_Line_Starts_Pending_With_Zero_Shortfall()
    {
        var line = NewLine();
        Assert.Equal(CookConsumeLineStatus.Pending, line.Status);
        Assert.Equal(0m, line.Shortfall);
    }

    [Fact]
    public void MarkDeferredUnitGap_Sets_Status_And_Full_Outstanding_Shortfall()
    {
        var line = NewLine(quantity: 150m);

        line.MarkDeferredUnitGap();

        Assert.Equal(CookConsumeLineStatus.DeferredUnitGap, line.Status);
        // Nothing consumed — the full quantity is owed until a conversion lands.
        Assert.Equal(150m, line.Shortfall);
    }

    [Fact]
    public void MarkApplied_After_Deferred_Overwrites_Status_And_Shortfall()
    {
        var line = NewLine(quantity: 150m);
        line.MarkDeferredUnitGap();

        line.MarkApplied(20m); // conversion landed; retro-apply reports a 20 residual shortfall

        Assert.Equal(CookConsumeLineStatus.Applied, line.Status);
        Assert.Equal(20m, line.Shortfall);
    }

    [Fact]
    public void MarkSupersededByCount_Voids_A_Deferred_Line()
    {
        var line = NewLine(quantity: 90m);
        line.MarkDeferredUnitGap();

        line.MarkSupersededByCount();

        Assert.Equal(CookConsumeLineStatus.SupersededByCount, line.Status);
        Assert.Equal(0m, line.Shortfall);
    }

    [Theory]
    [InlineData(CookConsumeLineStatus.Pending)]
    [InlineData(CookConsumeLineStatus.Applied)]
    [InlineData(CookConsumeLineStatus.Shorted)]
    public void MarkSupersededByCount_Is_NoOp_For_Non_Deferred_Lines(CookConsumeLineStatus start)
    {
        var line = NewLine(quantity: 40m);
        switch (start)
        {
            case CookConsumeLineStatus.Applied: line.MarkApplied(5m); break;
            case CookConsumeLineStatus.Shorted: line.MarkShorted(); break;
            // Pending: leave as created.
        }

        line.MarkSupersededByCount();

        // A non-deferred line must not be voided — only a pending deferred consume can be superseded.
        Assert.Equal(start, line.Status);
    }

    [Theory]
    [InlineData(CookConsumeLineStatus.Pending, "Pending")]
    [InlineData(CookConsumeLineStatus.Applied, "Applied")]
    [InlineData(CookConsumeLineStatus.Shorted, "Shorted")]
    [InlineData(CookConsumeLineStatus.DeferredUnitGap, "DeferredUnitGap")]
    [InlineData(CookConsumeLineStatus.SupersededByCount, "SupersededByCount")]
    public void Status_Text_RoundTrips(CookConsumeLineStatus status, string expected)
    {
        var db = status.ToDbValue();
        Assert.Equal(expected, db);
        Assert.Equal(status, CookConsumeLineStatusExtensions.Parse(db));
    }
}
