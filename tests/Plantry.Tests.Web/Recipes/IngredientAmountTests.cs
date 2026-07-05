using Plantry.Web.Pages.Recipes;

namespace Plantry.Tests.Web.Recipes;

// Unit tests for the canonical ingredient-amount formatter (bead plantry-jun6). Pins the acceptance
// examples and the shared trailing-zero rule; the JS twin (wwwroot/js/__tests__/ingredient-amount.test.js)
// mirrors these so server- and client-rendered amounts agree.
public class IngredientAmountTests
{
    [Theory]
    // Acceptance examples from the ticket.
    [InlineData("500.000", "500")]
    [InlineData("1.50", "1.5")]
    [InlineData("1", "1")]
    // Whole values never carry a trailing point or zeros.
    [InlineData("500", "500")]
    [InlineData("0", "0")]
    [InlineData("0.0", "0")]
    // Real fractional precision is preserved (up to MaxDecimals).
    [InlineData("0.125", "0.125")]
    [InlineData("2.5", "2.5")]
    [InlineData("1.2500", "1.25")]
    [InlineData("10.0100", "10.01")]
    public void Format_strips_trailing_zeros(string input, string expected)
    {
        var value = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, IngredientAmount.Format(value));
    }

    [Theory]
    // Values carrying more than MaxDecimals fractional digits are rounded (away from zero) then cleaned.
    [InlineData("33.33333", "33.3333")]
    [InlineData("66.66666", "66.6667")]
    [InlineData("0.00005", "0.0001")]
    public void Format_rounds_to_max_decimals(string input, string expected)
    {
        var value = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, IngredientAmount.Format(value));
    }

    [Fact]
    public void MaxDecimals_is_four_matching_the_js_twin()
    {
        Assert.Equal(4, IngredientAmount.MaxDecimals);
    }
}
