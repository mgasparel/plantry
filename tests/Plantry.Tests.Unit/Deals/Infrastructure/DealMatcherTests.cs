using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;

namespace Plantry.Tests.Unit.Deals.Infrastructure;

/// <summary>
/// L1 tests for <see cref="DealMatcher"/>'s pure <c>MapResponse</c> mapping — exercised against recorded
/// model output with no live API call (mirrors <c>GeminiReceiptParserTests</c>). Covers the untrusted-match
/// contract (ADR-007): high/low/none preserved, reasoning captured, a suggested id copied verbatim from the
/// candidate set kept but an invented/out-of-set id dropped (never committed), markdown-fence stripping, and
/// the soft-fail paths (empty / malformed JSON degrade to <see cref="MatchProposal.Unmatched"/>, never throw).
/// The adapter surface returns a proposal only and can never produce a <c>product_id</c> write.
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
    public void Maps_A_High_Confidence_Match_With_A_Valid_Candidate_Id()
    {
        var response = $$"""
            {
              "suggested_product_id": "{{MilkId}}",
              "confidence": "high",
              "reasoning": "Same 2% whole milk, matching brand and size."
            }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Equal(MilkId, proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposal.Confidence);
        Assert.Equal("Same 2% whole milk, matching brand and size.", proposal.Reasoning);
    }

    [Fact]
    public void Preserves_Low_Confidence()
    {
        var response = $$"""
            { "suggested_product_id": "{{CheeseId}}", "confidence": "low", "reasoning": "Plausible but the size differs." }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Equal(CheeseId, proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.Low, proposal.Confidence);
        Assert.Equal("Plausible but the size differs.", proposal.Reasoning);
    }

    [Fact]
    public void None_Confidence_With_Null_Id_Is_An_Unmatched_Proposal()
    {
        var response = """
            { "suggested_product_id": null, "confidence": "none", "reasoning": "Nothing in the catalog matches." }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Null(proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposal.Confidence);
        Assert.Equal("Nothing in the catalog matches.", proposal.Reasoning);
    }

    [Fact]
    public void Drops_An_Id_Not_In_The_Candidate_Set_And_Forces_None_But_Keeps_Reasoning()
    {
        // The model invented an id that is not one of the passed-in candidates, yet claimed "high".
        // ADR-007: the invented id must be dropped, and confidence cannot stay positive for no product.
        var invented = Guid.Parse("0193b4a0-9999-7000-8000-000000000999");
        var response = $$"""
            {
              "suggested_product_id": "{{invented}}",
              "confidence": "high",
              "reasoning": "Looks like store-brand milk."
            }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Null(proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposal.Confidence);
        Assert.Equal("Looks like store-brand milk.", proposal.Reasoning);
    }

    [Fact]
    public void Strips_Markdown_Fences_Before_Parsing()
    {
        var response = $$"""
            ```json
            { "suggested_product_id": "{{MilkId}}", "confidence": "high", "reasoning": "Exact match." }
            ```
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Equal(MilkId, proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposal.Confidence);
    }

    [Fact]
    public void Unknown_Confidence_Label_Maps_To_None()
    {
        var response = $$"""
            { "suggested_product_id": "{{MilkId}}", "confidence": "maybe", "reasoning": "Unsure." }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        // The id is valid, but an unrecognised label is never trusted as a positive confidence.
        Assert.Null(proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposal.Confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"suggested_product_id\": ")]
    [InlineData("[]")]
    public void Malformed_Or_Empty_Response_Soft_Fails_To_Unmatched_Without_Throwing(string? raw)
    {
        var proposal = DealMatcher.MapResponse(raw, Candidates);

        Assert.Null(proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, proposal.Confidence);
        Assert.Null(proposal.Reasoning);
    }

    [Fact]
    public void Missing_Reasoning_Is_Null_Not_A_Throw()
    {
        var response = $$"""
            { "suggested_product_id": "{{MilkId}}", "confidence": "high" }
            """;

        var proposal = DealMatcher.MapResponse(response, Candidates);

        Assert.Equal(MilkId, proposal.SuggestedProductId);
        Assert.Equal(MatchConfidence.High, proposal.Confidence);
        Assert.Null(proposal.Reasoning);
    }

    [Fact]
    public void ConfidenceScore_Maps_Enum_To_Histogram_Buckets()
    {
        Assert.Equal(1.0, DealMatcher.ConfidenceScore(MatchConfidence.High));
        Assert.Equal(0.5, DealMatcher.ConfidenceScore(MatchConfidence.Low));
        Assert.Equal(0.0, DealMatcher.ConfidenceScore(MatchConfidence.None));
    }
}
