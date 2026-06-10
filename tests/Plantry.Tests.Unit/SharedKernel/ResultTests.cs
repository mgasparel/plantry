using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Error_None_Has_Empty_Code_And_Description()
    {
        Assert.Equal(string.Empty, Error.None.Code);
        Assert.Equal(string.Empty, Error.None.Description);
    }

    [Fact]
    public void Error_NotFound_Has_Correct_Code_And_Description()
    {
        Assert.Equal("NotFound", Error.NotFound.Code);
        Assert.Equal("The requested resource was not found.", Error.NotFound.Description);
    }

    [Fact]
    public void Error_Unauthorized_Has_Correct_Code_And_Description()
    {
        Assert.Equal("Unauthorized", Error.Unauthorized.Code);
        Assert.Equal("Access denied.", Error.Unauthorized.Description);
    }

    [Fact]
    public void Error_Conflict_Has_Correct_Code_And_Description()
    {
        Assert.Equal("Conflict", Error.Conflict.Code);
        Assert.Equal("A conflict occurred.", Error.Conflict.Description);
    }

    [Fact]
    public void Result_T_Success_IsSuccess_True()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Result_T_Failure_IsFailure_True()
    {
        var result = Result<int>.Failure(Error.NotFound);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound, result.Error);
    }

    [Fact]
    public void Result_Success_IsSuccess_True()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void Result_Failure_IsFailure_True()
    {
        var result = Result.Failure(Error.Conflict);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(Error.Conflict, result.Error);
    }
}
