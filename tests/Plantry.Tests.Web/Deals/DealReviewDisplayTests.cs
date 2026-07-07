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
}
