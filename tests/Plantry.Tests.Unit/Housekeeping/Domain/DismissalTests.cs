using Plantry.Housekeeping.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Housekeeping.Domain;

/// <summary>L1 unit tests for the <see cref="Dismissal"/> aggregate (tidy-up.md T5).</summary>
public sealed class DismissalTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    [Fact(DisplayName = "Create — captures household, detector, subject, fingerprint, and dismissed-at from the clock")]
    public void Create_CapturesAllFields()
    {
        var clock = new TestClock();

        var dismissal = Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1", clock);

        Assert.Equal(Household, dismissal.HouseholdId);
        Assert.Equal(DetectorId.StockUnitUnconvertible, dismissal.DetectorId);
        Assert.Equal(SubjectId, dismissal.SubjectId);
        Assert.Equal("fp-1", dismissal.FactsFingerprint);
        Assert.Equal(clock.UtcNow, dismissal.DismissedAtUtc);
    }

    [Theory(DisplayName = "Create — blank fingerprint is rejected")]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankFingerprint_Throws(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, blank, new TestClock()));
    }

    [Fact(DisplayName = "Supersede — updates fingerprint and dismissed-at in place, keeps identity/key fields")]
    public void Supersede_UpdatesFingerprintAndTimestamp_KeepsKey()
    {
        var clock = new TestClock();
        var dismissal = Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1", clock);
        var originalId = dismissal.Id;

        clock.Advance(TimeSpan.FromDays(3));
        dismissal.Supersede("fp-2", clock);

        Assert.Equal(originalId, dismissal.Id); // same row, not a new tombstone
        Assert.Equal(Household, dismissal.HouseholdId);
        Assert.Equal(SubjectId, dismissal.SubjectId);
        Assert.Equal("fp-2", dismissal.FactsFingerprint);
        Assert.Equal(clock.UtcNow, dismissal.DismissedAtUtc);
    }

    [Theory(DisplayName = "Supersede — blank fingerprint is rejected")]
    [InlineData("")]
    [InlineData("  ")]
    public void Supersede_BlankFingerprint_Throws(string blank)
    {
        var dismissal = Dismissal.Create(Household, DetectorId.StockUnitUnconvertible, SubjectId, "fp-1", new TestClock());

        Assert.Throws<ArgumentException>(() => dismissal.Supersede(blank, new TestClock()));
    }
}
