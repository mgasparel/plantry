using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>
/// L1 unit tests for <see cref="FlyerImport"/> (§4): PullStatus monotonicity (DD12) and the
/// <c>raw_flyer</c> set-once invariant (DD6).
/// </summary>
public sealed class FlyerImportTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)).Value;

    private static FlyerImport NewImport(TestClock clock) =>
        FlyerImport.Start(Household, Store, "flyer-1", contentHash: null, Window(), "{\"raw\":true}", clock);

    [Fact(DisplayName = "Start opens the import in Pulling with the raw payload quarantined")]
    public void Start_OpensPulling()
    {
        var clock = new TestClock();
        var import = NewImport(clock);

        Assert.Equal(PullStatus.Pulling, import.Status);
        Assert.Equal("{\"raw\":true}", import.RawFlyer);
        Assert.Equal(clock.UtcNow, import.PulledAt);
        Assert.Null(import.ParsedAt);
    }

    [Fact(DisplayName = "MarkParsed transitions Pulling → Parsed and stamps parsed_at")]
    public void MarkParsed_TransitionsFromPulling()
    {
        var clock = new TestClock();
        var import = NewImport(clock);

        var result = import.MarkParsed(clock.Advance(TimeSpan.FromMinutes(5)));

        Assert.True(result.IsSuccess);
        Assert.Equal(PullStatus.Parsed, import.Status);
        Assert.Equal(clock.UtcNow, import.ParsedAt);
    }

    [Fact(DisplayName = "MarkFailed transitions Pulling → Failed and records the error detail")]
    public void MarkFailed_TransitionsFromPulling()
    {
        var import = NewImport(new TestClock());

        var result = import.MarkFailed("Flipp unreachable", new TestClock());

        Assert.True(result.IsSuccess);
        Assert.Equal(PullStatus.Failed, import.Status);
        Assert.Equal("Flipp unreachable", import.ErrorDetail);
    }

    [Fact(DisplayName = "PullStatus is monotonic: cannot re-transition once Parsed (DD12)")]
    public void MarkParsed_IsMonotonic()
    {
        var import = NewImport(new TestClock());
        import.MarkParsed(new TestClock());

        var reParse = import.MarkParsed(new TestClock());
        var toFailed = import.MarkFailed("late error", new TestClock());

        Assert.True(reParse.IsFailure);
        Assert.Equal(FlyerImport.NotPulling, reParse.Error);
        Assert.True(toFailed.IsFailure);
        Assert.Equal(PullStatus.Parsed, import.Status);
    }

    [Fact(DisplayName = "PullStatus is monotonic: cannot re-transition once Failed (DD12)")]
    public void MarkFailed_IsMonotonic()
    {
        var import = NewImport(new TestClock());
        import.MarkFailed("boom", new TestClock());

        var toParsed = import.MarkParsed(new TestClock());

        Assert.True(toParsed.IsFailure);
        Assert.Equal(PullStatus.Failed, import.Status);
    }

    [Fact(DisplayName = "raw_flyer is set-once: a re-set after parse fails (DD6)")]
    public void RawFlyer_IsSetOnce()
    {
        var import = NewImport(new TestClock());
        import.MarkParsed(new TestClock());

        var result = import.SetRawFlyer("{\"tampered\":true}");

        Assert.True(result.IsFailure);
        Assert.Equal(FlyerImport.RawFlyerAlreadySet, result.Error);
        Assert.Equal("{\"raw\":true}", import.RawFlyer);
    }
}
