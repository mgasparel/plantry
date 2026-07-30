using Plantry.Web.Pages.Recipes;

namespace Plantry.Tests.Web.Recipes;

// Unit tests for RazorJs.Literal (plantry-97jd — the fourth recurrence of the Html.Raw/
// attribute-truncation defect class; prior: plantry-gcpb, plantry-wcmg, plantry-qrg7). Pins the
// JS-unicode escape chain the owner selected over backslash-escaping (doesn't survive HTML attribute
// tokenization) and &quot; (context-fragile) — see the investigation record on plantry-97jd.
public class RazorJsTests
{
    [Theory]
    // Passthrough: numeric/glyph content the live callers actually feed the helper is untouched.
    [InlineData("400", "'400'")]
    [InlineData("1½", "'1½'")]
    [InlineData("≈ ½ batch", "'≈ ½ batch'")]
    // Pre-existing escaping, unchanged by this fix.
    [InlineData("it's", "'it\\'s'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("\\'", "'\\\\\\''")]
    // New HTML-significant-character escaping.
    [InlineData("1\" jar", "'1\\u0022 jar'")]
    [InlineData("Tom & Jerry", "'Tom \\u0026 Jerry'")]
    [InlineData("&quot;", "'\\u0026quot;'")]
    [InlineData("a<b", "'a\\u003Cb'")]
    [InlineData("1\" & <b>", "'1\\u0022 \\u0026 \\u003Cb>'")]
    public void Literal_produces_expected_js_source(string input, string expectedJsSource)
    {
        Assert.Equal(expectedJsSource, RazorJs.Literal(input));
    }

    [Theory]
    [InlineData("400")]
    [InlineData("1½")]
    [InlineData("≈ ½ batch")]
    [InlineData("it's")]
    [InlineData("a\\b")]
    [InlineData("\\'")]
    [InlineData("1\" jar")]
    [InlineData("Tom & Jerry")]
    [InlineData("&quot;")]
    [InlineData("a<b")]
    [InlineData("1\" & <b>")]
    public void Literal_output_never_contains_a_raw_html_significant_character(string input)
    {
        // The property the fix exists for: whatever comes in, the emitted JS source must contain no
        // raw '"', '&', or '<' — those are exactly the characters that can truncate or reinterpret the
        // double-quoted HTML attribute this literal is spliced into via @Html.Raw.
        var result = RazorJs.Literal(input);

        Assert.DoesNotContain('"', result);
        Assert.DoesNotContain('&', result);
        Assert.DoesNotContain('<', result);
    }
}
