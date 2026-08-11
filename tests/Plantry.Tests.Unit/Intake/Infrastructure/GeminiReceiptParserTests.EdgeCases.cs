using Plantry.Intake.Infrastructure;

namespace Plantry.Tests.Unit.Intake.Infrastructure;

public sealed class GeminiReceiptParserEdgeCasesTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Soft_Fails_When_Root_Is_Not_A_Json_Object(string json)
    {
        var result = GeminiReceiptParser.MapResponse(json);
        Assert.True(result.HasError);
        Assert.Empty(result.Lines);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"item\"")]
    public void Soft_Fails_When_A_Line_Is_Not_A_Json_Object(string line)
    {
        var result = GeminiReceiptParser.MapResponse($"{{\"lines\":[{line}]}}");
        Assert.True(result.HasError);
        Assert.Empty(result.Lines);
    }

    [Theory]
    [InlineData("2026-06-07", true)]
    [InlineData("06/07/2026", false)]
    [InlineData("2026-6-7", false)]
    public void Date_Requires_The_Locale_Neutral_Contract(string value, bool expected)
    {
        var result = GeminiReceiptParser.MapResponse($"{{\"purchase_date\":\"{value}\",\"lines\":[]}}");
        Assert.Equal(expected, result.Metadata!.PurchaseDate is not null);
    }

    [Theory]
    [InlineData("14:34", true)]
    [InlineData("2:34 PM", false)]
    [InlineData("14.34", false)]
    public void Time_Requires_The_Locale_Neutral_Contract(string value, bool expected)
    {
        var result = GeminiReceiptParser.MapResponse($"{{\"purchase_time\":\"{value}\",\"lines\":[]}}");
        Assert.Equal(expected, result.Metadata!.PurchaseTime is not null);
    }
}
