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

    // ── Alternatives mapping ──────────────────────────────────────────────────────────────────────

    private const string ResponseWithAlternatives = """
        {
          "merchant": "Super Mart",
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "BECE MARG W-AVOC",
              "suggested_product_id": "0193b4a0-aaaa-7000-8000-000000000001",
              "suggested_product_name": "Butter",
              "quantity": 1,
              "unit": null,
              "price": 7.99,
              "confidence": "high",
              "alternatives": [
                { "product_id": "0193b4a0-bbbb-7000-8000-000000000002", "product_name": "Margarine", "confidence": 0.62 },
                { "product_id": "0193b4a0-cccc-7000-8000-000000000003", "product_name": "Avocado Spread", "confidence": 0.41 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Maps_Alternatives_Array_Into_ParsedLine_Alternatives()
    {
        var result = GeminiReceiptParser.MapResponse(ResponseWithAlternatives);

        Assert.False(result.HasError);
        var line = result.Lines[0];
        Assert.NotNull(line.Alternatives);
        Assert.Equal(2, line.Alternatives.Count);

        var first = line.Alternatives[0];
        Assert.Equal(Guid.Parse("0193b4a0-bbbb-7000-8000-000000000002"), first.ProductId);
        Assert.Equal("Margarine", first.ProductName);
        Assert.Equal(0.62m, first.Confidence);

        var second = line.Alternatives[1];
        Assert.Equal(Guid.Parse("0193b4a0-cccc-7000-8000-000000000003"), second.ProductId);
        Assert.Equal("Avocado Spread", second.ProductName);
        Assert.Equal(0.41m, second.Confidence);
    }

    [Fact]
    public void Caps_Alternatives_At_Three()
    {
        var responseWithFour = """
            {
              "merchant": null,
              "lines": [
                {
                  "line_no": 1,
                  "receipt_text": "TOMATO",
                  "suggested_product_id": "0193b4a0-1111-7000-8000-000000000001",
                  "suggested_product_name": "Cherry Tomato",
                  "quantity": 1, "unit": null, "price": 2.00, "confidence": "high",
                  "alternatives": [
                    { "product_id": "0193b4a0-2222-7000-8000-000000000002", "product_name": "Vine Tomato",   "confidence": 0.80 },
                    { "product_id": "0193b4a0-3333-7000-8000-000000000003", "product_name": "Roma Tomato",   "confidence": 0.70 },
                    { "product_id": "0193b4a0-4444-7000-8000-000000000004", "product_name": "Beefsteak Tomato", "confidence": 0.50 },
                    { "product_id": "0193b4a0-5555-7000-8000-000000000005", "product_name": "Plum Tomato",   "confidence": 0.30 }
                  ]
                }
              ]
            }
            """;

        var result = GeminiReceiptParser.MapResponse(responseWithFour);

        Assert.False(result.HasError);
        Assert.NotNull(result.Lines[0].Alternatives);
        Assert.Equal(3, result.Lines[0].Alternatives!.Count);
    }

    [Fact]
    public void Drops_Alternative_That_Duplicates_Primary_Id()
    {
        var primaryId = "0193b4a0-aaaa-7000-8000-000000000001";
        var responseWithDuplicate = $$"""
            {
              "merchant": null,
              "lines": [
                {
                  "line_no": 1,
                  "receipt_text": "BUTTER",
                  "suggested_product_id": "{{primaryId}}",
                  "suggested_product_name": "Butter",
                  "quantity": 1, "unit": null, "price": 5.00, "confidence": "high",
                  "alternatives": [
                    { "product_id": "{{primaryId}}", "product_name": "Butter", "confidence": 0.90 },
                    { "product_id": "0193b4a0-bbbb-7000-8000-000000000002", "product_name": "Margarine", "confidence": 0.60 }
                  ]
                }
              ]
            }
            """;

        var result = GeminiReceiptParser.MapResponse(responseWithDuplicate);

        Assert.False(result.HasError);
        var alts = result.Lines[0].Alternatives;
        Assert.NotNull(alts);
        Assert.Single(alts);
        Assert.Equal(Guid.Parse("0193b4a0-bbbb-7000-8000-000000000002"), alts[0].ProductId);
    }

    [Fact]
    public void Skips_Alternative_With_Null_Or_Malformed_Product_Id()
    {
        var responseWithBadIds = """
            {
              "merchant": null,
              "lines": [
                {
                  "line_no": 1,
                  "receipt_text": "ITEM",
                  "suggested_product_id": null,
                  "suggested_product_name": null,
                  "quantity": 1, "unit": null, "price": 1.00, "confidence": "none",
                  "alternatives": [
                    { "product_id": null,           "product_name": "No Id",       "confidence": 0.50 },
                    { "product_id": "not-a-guid",   "product_name": "Bad Guid",    "confidence": 0.40 },
                    { "product_id": "0193b4a0-dddd-7000-8000-000000000004", "product_name": "Valid", "confidence": 0.30 }
                  ]
                }
              ]
            }
            """;

        var result = GeminiReceiptParser.MapResponse(responseWithBadIds);

        Assert.False(result.HasError);
        var alts = result.Lines[0].Alternatives;
        Assert.NotNull(alts);
        Assert.Single(alts);
        Assert.Equal("Valid", alts[0].ProductName);
    }

    [Fact]
    public void Returns_Null_Alternatives_When_Array_Absent()
    {
        // The existing RecordedResponse fixture has no "alternatives" key.
        var result = GeminiReceiptParser.MapResponse(RecordedResponse);

        Assert.False(result.HasError);
        Assert.All(result.Lines, l => Assert.Null(l.Alternatives));
    }
}
