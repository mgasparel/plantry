using Plantry.Deals.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>L1 unit tests for <see cref="ValidityWindow"/> — the DD10 <c>valid_from &lt;= valid_to</c> invariant.</summary>
public sealed class ValidityWindowTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Jan7 = new(2026, 1, 7);

    [Fact(DisplayName = "Create succeeds when valid_from <= valid_to")]
    public void Create_ForwardRange_Succeeds()
    {
        var result = ValidityWindow.Create(Jan1, Jan7);

        Assert.True(result.IsSuccess);
        Assert.Equal(Jan1, result.Value.ValidFrom);
        Assert.Equal(Jan7, result.Value.ValidTo);
    }

    [Fact(DisplayName = "Create succeeds for a single-day window (valid_from == valid_to)")]
    public void Create_SameDay_Succeeds()
    {
        Assert.True(ValidityWindow.Create(Jan1, Jan1).IsSuccess);
    }

    [Fact(DisplayName = "Create fails for an inverted range (valid_from > valid_to)")]
    public void Create_InvertedRange_Fails()
    {
        var result = ValidityWindow.Create(Jan7, Jan1);

        Assert.True(result.IsFailure);
        Assert.Equal(ValidityWindow.InvalidRange, result.Error);
    }

    [Theory(DisplayName = "Contains is inclusive of both endpoints and excludes out-of-window dates")]
    [InlineData("2026-01-01", true)]
    [InlineData("2026-01-04", true)]
    [InlineData("2026-01-07", true)]
    [InlineData("2025-12-31", false)]
    [InlineData("2026-01-08", false)]
    public void Contains_IsInclusive(string dateText, bool expected)
    {
        var window = ValidityWindow.Create(Jan1, Jan7).Value;

        Assert.Equal(expected, window.Contains(DateOnly.Parse(dateText)));
    }
}
