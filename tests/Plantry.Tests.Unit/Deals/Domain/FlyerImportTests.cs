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

    [Fact(DisplayName = "MarkParsed transitions Pulling → Parsed, stamps parsed_at, and emits FlyerImported")]
    public void MarkParsed_TransitionsFromPulling()
    {
        var clock = new TestClock();
        var import = NewImport(clock);

        var result = import.MarkParsed(pendingCount: 3, clock.Advance(TimeSpan.FromMinutes(5)));

        Assert.True(result.IsSuccess);
        Assert.Equal(PullStatus.Parsed, import.Status);
        Assert.Equal(clock.UtcNow, import.ParsedAt);

        var emitted = Assert.IsType<FlyerImportedEvent>(Assert.Single(import.DomainEvents));
        Assert.Equal(import.Id, emitted.FlyerImportId);
        Assert.Equal(Store, emitted.StoreId);
        Assert.Equal(3, emitted.PendingCount);
    }

    [Fact(DisplayName = "RecordRepull on a Parsed import refreshes content hash + window and re-emits FlyerImported (DD5/DD13)")]
    public void RecordRepull_RefreshesBookkeeping_AndReemits()
    {
        var clock = new TestClock();
        var import = NewImport(clock);
        import.MarkParsed(pendingCount: 1, clock);
        import.ClearDomainEvents();

        var newWindow = ValidityWindow.Create(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 8)).Value;
        var result = import.RecordRepull([1, 2, 3], newWindow, pendingCount: 2, clock.Advance(TimeSpan.FromDays(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(PullStatus.Parsed, import.Status); // status untouched
        Assert.Equal("{\"raw\":true}", import.RawFlyer);  // raw_flyer immutable (DD6)
        Assert.Equal(new byte[] { 1, 2, 3 }, import.ContentHash);
        Assert.Equal(newWindow, import.ValidityWindow);
        var emitted = Assert.IsType<FlyerImportedEvent>(Assert.Single(import.DomainEvents));
        Assert.Equal(2, emitted.PendingCount);
    }

    [Fact(DisplayName = "RecordRepull is rejected on an import that never Parsed (DD12)")]
    public void RecordRepull_RequiresParsed()
    {
        var import = NewImport(new TestClock());

        var result = import.RecordRepull([9], Window(), pendingCount: 0, new TestClock());

        Assert.True(result.IsFailure);
        Assert.Equal(FlyerImport.NotParsed, result.Error);
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
        import.MarkParsed(pendingCount: 0, new TestClock());

        var reParse = import.MarkParsed(pendingCount: 0, new TestClock());
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

        var toParsed = import.MarkParsed(pendingCount: 0, new TestClock());

        Assert.True(toParsed.IsFailure);
        Assert.Equal(PullStatus.Failed, import.Status);
    }

    [Fact(DisplayName = "raw_flyer is set-once: a re-set after parse fails (DD6)")]
    public void RawFlyer_IsSetOnce()
    {
        var import = NewImport(new TestClock());
        import.MarkParsed(pendingCount: 0, new TestClock());

        var result = import.SetRawFlyer("{\"tampered\":true}");

        Assert.True(result.IsFailure);
        Assert.Equal(FlyerImport.RawFlyerAlreadySet, result.Error);
        Assert.Equal("{\"raw\":true}", import.RawFlyer);
    }
}
