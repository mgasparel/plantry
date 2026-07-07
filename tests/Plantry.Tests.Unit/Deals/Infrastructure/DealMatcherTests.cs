using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;

namespace Plantry.Tests.Unit.Deals.Infrastructure;

/// <summary>
/// L1 tests for <see cref="DealMatcher"/>'s pure <c>MapBatchResponse</c> mapping — exercised against recorded
/// model output with no live API call (mirrors <c>GeminiReceiptParserTests</c>). Covers the untrusted-match
/// contract (ADR-007) applied PER ITEM: high/low/none preserved, reasoning captured, a suggested id copied
/// verbatim from the candidate set kept but an invented/out-of-set id dropped (never committed), markdown-fence
/// stripping, and the per-item soft-fail paths (a malformed element, a missing index, or a duplicate index
/// unmatch only that position; a non-array/unparseable payload unmatches the whole chunk) — always returning a
/// positionally-aligned list of the requested length and never throwing. The adapter surface returns proposals
/// only and can never produce a <c>product_id</c> write.
/// </summary>
public sealed class DealMatcherTests
{
    private static readonly Guid MilkId = Guid.Parse("0193b4a0-1111-7000-8000-000000000001");
    private static readonly Guid CheeseId = Guid.Parse("0193b4a0-1111-7000-8000-000000000002");

    private static readonly IReadOnlyList<ProductCandidate> Candidates =
    [
        new(MilkId, "Whole Milk 2%", "Beatrice"),
        new(CheeseId, "Cheddar Cheese", "Black Diamond"),
    ];

    [Fact]
    public void Maps_Each_Item_Positionally_With_Valid_Ids_And_Confidences()
    {
        var response = $$"""
            [
              { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Same 2% whole milk." },
              { "index": 1, "suggested_product_id": "{{CheeseId}}", "confidence": "low", "reasoning": "Plausible but the size differs." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Equal(2, proposals.Count);
        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposals[0].Confidence);
        Assert.Equal("Same 2% whole milk.", proposals[0].Reasoning);
        Assert.Equal(CheeseId, proposals[1].SuggestedProductId);
        Assert.Equal(MatchConfidence.Low, proposals[1].Confidence);
        Assert.Equal("Plausible but the size differs.", proposals[1].Reasoning);
    }

    [Fact]
    public void Respects_Index_Order_Even_When_Elements_Are_Shuffled()
    {
        // The model returned the items out of order; the mapper must place each by its declared index.
        var response = $$"""
            [
              { "index": 1, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Cheese." },
              { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Milk." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(CheeseId, proposals[1].SuggestedProductId);
    }

    [Fact]
    public void None_Confidence_With_Null_Id_Is_An_Unmatched_Item()
    {
        var response = """
            [ { "index": 0, "suggested_product_id": null, "confidence": "none", "reasoning": "Nothing matches." } ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 1);

        Assert.Null(proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[0].Confidence);
        Assert.Equal("Nothing matches.", proposals[0].Reasoning);
    }

    [Fact]
    public void Drops_An_Invented_Id_Not_In_The_Candidate_Set_But_Keeps_Reasoning()
    {
        // ADR-007: the model invented an id that is not one of the passed-in candidates, yet claimed "high".
        // That single item must drop the invented id and collapse to None — its siblings are unaffected.
        var invented = Guid.Parse("0193b4a0-9999-7000-8000-000000000999");
        var response = $$"""
            [
              { "index": 0, "suggested_product_id": "{{invented}}", "confidence": "high", "reasoning": "Looks like store-brand milk." },
              { "index": 1, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Cheese." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Null(proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[0].Confidence);
        Assert.Equal("Looks like store-brand milk.", proposals[0].Reasoning);
        // Sibling item is untouched by the neighbour's invention.
        Assert.Equal(CheeseId, proposals[1].SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposals[1].Confidence);
    }

    [Fact]
    public void A_Missing_Index_Leaves_That_Item_Unmatched()
    {
        // Two items were requested but the model only returned index 0. Index 1 must default to Unmatched.
        var response = $$"""
            [ { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Milk." } ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Equal(2, proposals.Count);
        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Null(proposals[1].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[1].Confidence);
        Assert.Null(proposals[1].Reasoning);
    }

    [Fact]
    public void An_Element_With_No_Index_Field_Is_Skipped_Not_Applied_To_Position_Zero()
    {
        // A malformed element (no "index") must not silently land on item 0 — it is dropped and both items
        // stay Unmatched.
        var response = $$"""
            [ { "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Milk." } ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Null(proposals[0].SuggestedProductId);
        Assert.Null(proposals[1].SuggestedProductId);
    }

    [Fact]
    public void A_Malformed_Non_Object_Element_Is_Skipped_Without_Poisoning_Others()
    {
        var response = $$"""
            [
              "not an object",
              { "index": 1, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Cheese." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Null(proposals[0].SuggestedProductId);           // never addressed → Unmatched
        Assert.Equal(CheeseId, proposals[1].SuggestedProductId); // valid sibling survives
    }

    [Fact]
    public void A_Duplicate_Index_Forces_That_Item_Back_To_Unmatched()
    {
        // Two elements both claim index 0 — trust neither. Item 0 collapses to Unmatched; item 1 is fine.
        var response = $$"""
            [
              { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Milk." },
              { "index": 0, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Also milk?" },
              { "index": 1, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Cheese." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Null(proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[0].Confidence);
        Assert.Equal(CheeseId, proposals[1].SuggestedProductId);
    }

    [Fact]
    public void An_Out_Of_Range_Index_Is_Ignored()
    {
        var response = $$"""
            [
              { "index": 5, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Milk." },
              { "index": 0, "suggested_product_id": "{{CheeseId}}", "confidence": "high", "reasoning": "Cheese." }
            ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 2);

        Assert.Equal(CheeseId, proposals[0].SuggestedProductId);
        Assert.Null(proposals[1].SuggestedProductId); // index 5 discarded; 1 never addressed
    }

    [Fact]
    public void Strips_Markdown_Fences_Before_Parsing()
    {
        var response = $$"""
            ```json
            [ { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Exact match." } ]
            ```
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 1);

        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposals[0].Confidence);
    }

    [Fact]
    public void Unknown_Confidence_Label_Maps_To_None_And_Drops_The_Id()
    {
        var response = $$"""
            [ { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "maybe", "reasoning": "Unsure." } ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 1);

        // The id is valid, but an unrecognised label is never trusted as a positive confidence.
        Assert.Null(proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposals[0].Confidence);
    }

    [Fact]
    public void Missing_Reasoning_Is_Null_Not_A_Throw()
    {
        var response = $$"""
            [ { "index": 0, "suggested_product_id": "{{MilkId}}", "confidence": "high" } ]
            """;

        var proposals = DealMatcher.MapBatchResponse(response, Candidates, 1);

        Assert.Equal(MilkId, proposals[0].SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposals[0].Confidence);
        Assert.Null(proposals[0].Reasoning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[ { \"index\": 0, ")]
    [InlineData("{}")]  // an object, not the expected array → whole chunk unmatches
    public void Malformed_Empty_Or_Non_Array_Response_Soft_Fails_The_Whole_Chunk(string? raw)
    {
        var proposals = DealMatcher.MapBatchResponse(raw, Candidates, 3);

        Assert.Equal(3, proposals.Count);
        Assert.All(proposals, p =>
        {
            Assert.Null(p.SuggestedProductId);
            Assert.Equal(MatchConfidence.None, p.Confidence);
            Assert.Null(p.Reasoning);
        });
    }

    [Fact]
    public void An_Empty_Expected_Count_Returns_An_Empty_List()
    {
        var proposals = DealMatcher.MapBatchResponse("[]", Candidates, 0);

        Assert.Empty(proposals);
    }

    [Fact]
    public void ParseConfidence_Maps_Labels_To_The_Enum()
    {
        Assert.Equal(MatchConfidence.High, DealMatcher.ParseConfidence("high"));
        Assert.Equal(MatchConfidence.Low, DealMatcher.ParseConfidence("low"));
        Assert.Equal(MatchConfidence.None, DealMatcher.ParseConfidence("none"));
        Assert.Equal(MatchConfidence.None, DealMatcher.ParseConfidence("garbage"));
        Assert.Equal(MatchConfidence.None, DealMatcher.ParseConfidence(null));
    }

    [Fact]
    public void ConfidenceScore_Maps_Enum_To_Histogram_Buckets()
    {
        Assert.Equal(1.0, DealMatcher.ConfidenceScore(MatchConfidence.High));
        Assert.Equal(0.5, DealMatcher.ConfidenceScore(MatchConfidence.Low));
        Assert.Equal(0.0, DealMatcher.ConfidenceScore(MatchConfidence.None));
    }
}
