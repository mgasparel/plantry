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

    // ── Weight→each estimate mapping (plantry-1mu) ──────────────────────────────────────────────

    private const string ResponseWithEachEstimate = """
        {
          "merchant": "Whole Foods Market",
          "lines": [
            {
              "line_no": 1,
              "receipt_text": "ORG BANANAS 1.34 lb @ 0.59/lb",
              "suggested_product_id": "0193b4a0-1111-7000-8000-000000000001",
              "suggested_product_name": "Bananas",
              "quantity": 1.34,
              "unit": "lb",
              "price": 0.79,
              "confidence": "high",
              "estimated_each_count": 7,
              "each_confidence": "high"
            },
            {
              "line_no": 2,
              "receipt_text": "BLACK FOREST HAM 0.35 lb",
              "suggested_product_id": "0193b4a0-1111-7000-8000-000000000002",
              "suggested_product_name": "Deli Ham",
              "quantity": 0.35,
              "unit": "lb",
              "price": 4.20,
              "confidence": "high",
              "estimated_each_count": null,
              "each_confidence": null
            }
          ]
        }
        """;

    [Fact]
    public void Maps_Estimated_Each_Count_And_Confidence_For_A_Weight_Priced_Each_Tracked_Line()
    {
        var result = GeminiReceiptParser.MapResponse(ResponseWithEachEstimate);

        Assert.False(result.HasError);

        var bananas = result.Lines[0];
        Assert.Equal(1.34m, bananas.Quantity);           // ground-truth weight preserved
        Assert.Equal("lb", bananas.UnitLabel);
        Assert.Equal(7m, bananas.EstimatedEachCount);
        Assert.Equal("high", bananas.EstimatedEachConfidence);

        // Deli ham is genuinely weight-tracked → no each-count estimate (never converted).
        var ham = result.Lines[1];
        Assert.Null(ham.EstimatedEachCount);
        Assert.Null(ham.EstimatedEachConfidence);
    }

    [Fact]
    public void Drops_A_Non_Positive_Estimated_Each_Count()
    {
        const string json = """
            { "merchant": "M", "lines": [ {
              "line_no": 1, "receipt_text": "X", "quantity": 1.0, "unit": "lb", "price": 1.0,
              "confidence": "high", "estimated_each_count": 0, "each_confidence": "low" } ] }
            """;

        var line = Assert.Single(GeminiReceiptParser.MapResponse(json).Lines);
        Assert.Null(line.EstimatedEachCount); // 0 is not a usable count → dropped
    }

    // ── Receipt metadata mapping ────────────────────────────────────────────────────────────────

    private const string ResponseWithMetadata = """
        {
          "merchant": "Real Canadian Superstore",
          "store_branch": "1000 Marine Dr, North Vancouver",
          "purchase_date": "2026-06-07",
          "purchase_time": "14:34",
          "subtotal": 39.60,
          "tax": 1.98,
          "total": 41.58,
          "payment": "VISA ****4471 APPROVED",
          "receipt_number": "TXN 0472 118 6620",
          "lines": []
        }
        """;

    [Fact]
    public void Maps_Receipt_Header_Metadata_When_Present()
    {
        var result = GeminiReceiptParser.MapResponse(ResponseWithMetadata);

        Assert.False(result.HasError);
        Assert.NotNull(result.Metadata);
        var m = result.Metadata!;
        Assert.Equal("1000 Marine Dr, North Vancouver", m.StoreBranch);
        Assert.Equal(new DateOnly(2026, 6, 7), m.PurchaseDate);
        Assert.Equal(new TimeOnly(14, 34), m.PurchaseTime);
        Assert.Equal(39.60m, m.Subtotal);
        Assert.Equal(1.98m, m.Tax);
        Assert.Equal(41.58m, m.Total);
        Assert.Equal("VISA ****4471 APPROVED", m.PaymentDescriptor);
        Assert.Equal("TXN 0472 118 6620", m.ReceiptNumber);
    }

    [Fact]
    public void Metadata_Fields_Are_Null_When_Absent_Or_Unparseable()
    {
        // The RecordedResponse fixture has none of the metadata keys; a bad date/time must drop to null
        // rather than throw (untrusted display data).
        var badShapes = """
            {"merchant":null,"purchase_date":"not-a-date","purchase_time":"99:99","lines":[]}
            """;

        var recorded = GeminiReceiptParser.MapResponse(RecordedResponse);
        Assert.NotNull(recorded.Metadata);
        Assert.Null(recorded.Metadata!.StoreBranch);
        Assert.Null(recorded.Metadata.Subtotal);
        Assert.Null(recorded.Metadata.PurchaseDate);

        var bad = GeminiReceiptParser.MapResponse(badShapes);
        Assert.False(bad.HasError);
        Assert.NotNull(bad.Metadata);
        Assert.Null(bad.Metadata!.PurchaseDate);
        Assert.Null(bad.Metadata.PurchaseTime);
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

    // ── purchase_date plausibility window (plantry-ag05) ────────────────────────────────────────────

    private static readonly DateOnly Upload = new(2026, 7, 19);

    [Theory]
    [InlineData("2026-07-19")] // the upload date itself
    [InlineData("2026-07-20")] // upload + 1 day — inclusive timezone-skew allowance
    [InlineData("2025-07-19")] // exactly one year before upload — inclusive past floor
    [InlineData("2026-03-01")] // an ordinary recent date
    public void Plausible_Dates_Are_Inside_The_Window(string date) =>
        Assert.True(GeminiReceiptParser.IsPlausiblePurchaseDate(DateOnly.Parse(date), Upload));

    [Theory]
    [InlineData("2026-07-21")] // two days after upload — past the +1-day allowance
    [InlineData("2027-07-19")] // a year in the future
    [InlineData("2025-07-18")] // one day past the one-year floor
    [InlineData("2019-07-26")] // the reported bug: a year-digit swap, ~7 years stale
    public void Implausible_Dates_Are_Outside_The_Window(string date) =>
        Assert.False(GeminiReceiptParser.IsPlausiblePurchaseDate(DateOnly.Parse(date), Upload));
}
