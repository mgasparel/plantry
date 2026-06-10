using Plantry.Intake.Infrastructure;

namespace Plantry.Tests.Unit.Intake.Infrastructure;

/// <summary>
/// L1 tests for <see cref="GeminiReceiptParser"/>'s pure <c>MapResponse</c> mapping — exercised against
/// recorded model output with no live API call. Covers markdown-fence stripping, the soft-fail paths
/// (empty / malformed JSON never throw), and verbatim preservation of each line's raw JSON (ACL).
/// </summary>
public sealed class GeminiReceiptParserTests
{
    private const string RecordedResponse = """
        {
          "merchant": "Whole Foods Market",
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "ORG BANANAS",
              "suggested_product_id": "0193b4a0-1111-7000-8000-000000000001",
              "suggested_product_name": "Bananas",
              "quantity": 1.2,
              "unit": "kg",
              "price": 1.74,
              "confidence": "high"
            },
            {
              "line_no": 2,
              "receipt_text": "ALMOND MILK",
              "suggested_product_id": null,
              "suggested_product_name": null,
              "quantity": 1,
              "unit": null,
              "price": 3.99,
              "confidence": "low"
            }
          ]
        }
        """;

    [Fact]
    public void Maps_A_Recorded_Response_Into_Merchant_And_Lines()
    {
        var result = GeminiReceiptParser.MapResponse(RecordedResponse);

        Assert.False(result.HasError);
        Assert.Equal("Whole Foods Market", result.MerchantText);
        Assert.Equal(2, result.Lines.Count);

        var bananas = result.Lines[0];
        Assert.Equal(1, bananas.LineNo);
        Assert.Equal("ORG BANANAS", bananas.ReceiptText);
        Assert.Equal(Guid.Parse("0193b4a0-1111-7000-8000-000000000001"), bananas.SuggestedProductId);
        Assert.Equal(1.2m, bananas.Quantity);
        Assert.Equal("kg", bananas.UnitLabel);
        Assert.Equal(1.74m, bananas.Price);
        Assert.Equal("high", bananas.Confidence);
        Assert.Contains("ORG BANANAS", bananas.RawJson); // raw payload preserved verbatim

        var milk = result.Lines[1];
        Assert.Null(milk.SuggestedProductId);
        Assert.Null(milk.UnitLabel);
    }

    [Fact]
    public void Strips_Markdown_Fences_Before_Parsing()
    {
        var fenced = "```json\n{\"merchant\":\"Aldi\",\"lines\":[]}\n```";

        var result = GeminiReceiptParser.MapResponse(fenced);

        Assert.False(result.HasError);
        Assert.Equal("Aldi", result.MerchantText);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Strips_A_Single_Line_Fenced_Payload()
    {
        var fenced = "```json {\"merchant\":\"Lidl\",\"lines\":[]} ```";

        var result = GeminiReceiptParser.MapResponse(fenced);

        Assert.False(result.HasError);
        Assert.Equal("Lidl", result.MerchantText);
    }

    [Fact]
    public void Soft_Fails_On_Empty_Content()
    {
        var result = GeminiReceiptParser.MapResponse("   ");

        Assert.True(result.HasError);
        Assert.Equal("AI returned an empty response.", result.ErrorMessage);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Soft_Fails_On_Malformed_Json_Without_Throwing()
    {
        var result = GeminiReceiptParser.MapResponse("{ this is not valid json ");

        Assert.True(result.HasError);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("unparseable", result.ErrorMessage);
    }

    [Fact]
    public void Falls_Back_To_Positional_Line_Numbers_When_Absent()
    {
        var noLineNos = """{"merchant":null,"lines":[{"receipt_text":"A"},{"receipt_text":"B"}]}""";

        var result = GeminiReceiptParser.MapResponse(noLineNos);

        Assert.False(result.HasError);
        Assert.Equal([1, 2], result.Lines.Select(l => l.LineNo));
    }
}
