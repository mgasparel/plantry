using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.Web.Pages.Deals;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// Unit coverage for the view-level display helpers (q9zr.2 / q9zr.10). Pins the title-casing boundary
/// rules — capitalise after start / space / dash / slash / paren, but never after an apostrophe — and the
/// flyer-noise predicate.
/// </summary>
public sealed class DealReviewDisplayTests
{
    [Theory]
    [InlineData("BUTTER CROISSANTS, 12'S", "Butter Croissants, 12's")]   // apostrophe: 's' stays lowercase
    [InlineData("FRANK'S HOT SAUCE 375ML", "Frank's Hot Sauce 375ml")]   // possessive apostrophe
    [InlineData("ALCAN ALUMINUM FOIL/GLAD PLASTIC WRAP", "Alcan Aluminum Foil/Glad Plastic Wrap")] // slash boundary
    [InlineData("ORANGE JUICE (NO PULP)", "Orange Juice (No Pulp)")]     // open-paren boundary
    [InlineData("DECAF-COFFEE", "Decaf-Coffee")]                          // hyphen boundary
    [InlineData("already lower", "Already Lower")]
    [InlineData("", "")]
    public void TitleCase_Applies_Boundary_Rules(string raw, string expected) =>
        Assert.Equal(expected, DealReviewDisplay.TitleCase(raw));

    [Theory]
    [InlineData(0.00, true)]
    [InlineData(-1.00, true)]
    [InlineData(0.01, false)]
    [InlineData(4.99, false)]
    public void IsNoise_Flags_NonPositive_Prices(double price, bool expected) =>
        Assert.Equal(expected, DealReviewDisplay.IsNoise((decimal)price));

    [Theory]
    [InlineData(1.0, "each", "/ each")]
    [InlineData(4.0, "L", "/ 4 L")]
    public void FormatPriceBasis_Renders_Quantity_And_Unit(double quantity, string unit, string expected) =>
        Assert.Equal(expected, DealReviewDisplay.FormatPriceBasis((decimal)quantity, unit));

    [Fact]
    public void FormatPriceBasis_Leaves_Unknown_Unit_Unqualified()
    {
        Assert.Equal("/ kg", DealReviewDisplay.FormatPriceBasis(null, "kg"));
        Assert.Null(DealReviewDisplay.FormatPriceBasis(4m, null));
    }

    [Fact]
    public void UnitMismatchHint_Requires_Different_Known_Unit_Ids()
    {
        var dealUnit = Guid.NewGuid();
        var productUnit = Guid.NewGuid();
        var view = new DealReviewView(
            DealId.New(), Guid.NewGuid(), "Store", "MANGOES CASE", null, null, 18m, 1m,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            MatchConfidence.High, null, Guid.NewGuid(), "Mangoes", DealStatus.Pending, false,
            dealUnit, null, [], productUnit, "crate", "each");

        Assert.True(view.HasKnownUnitMismatch);
        Assert.Contains("priced per crate", DealReviewDisplay.BuildUnitMismatchHint(view));

        var sameUnit = view with { SuggestedProductUnitId = dealUnit, SuggestedProductUnitCode = "crate" };
        Assert.False(sameUnit.HasKnownUnitMismatch);
        Assert.Null(DealReviewDisplay.BuildUnitMismatchHint(sameUnit));
    }

    // ── FormatPurchaseInterval (plantry-gtgl, Deals-review "you buy this every ~N" cadence copy) ──────────

    [Theory]
    [InlineData(3.0 / 24, "1 day")]     // 3 hours — rounds up to a minimum of "1 day", singular
    [InlineData(1, "1 day")]
    [InlineData(5, "5 days")]
    [InlineData(10, "1 week")]          // days/7 rounds to 1.4286 → 1 — singular, not "1 weeks"
    [InlineData(21, "3 weeks")]         // the ticket's canonical "every ~3 weeks" case
    [InlineData(69, "10 weeks")]
    [InlineData(70, "2 months")]
    [InlineData(365, "12 months")]
    public void FormatPurchaseInterval_Renders_Grammatical_Rough_Cadence(double days, string expected) =>
        Assert.Equal(expected, DealReviewDisplay.FormatPurchaseInterval(TimeSpan.FromDays(days)));
}
