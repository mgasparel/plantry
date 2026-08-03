using System.Net;
using System.Text.RegularExpressions;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render + POST assertions for the Recipe Details rating capture + household summary
/// (plantry-zlwp.3): the "Your rating" star-rating input, the household summary line
/// (rating-pill + popover), and the <c>OnPostRateAsync</c> rate/clear handler's OOB response.
/// Mirrors <c>RecipeDetailExpiredBadgeTests</c>' factory-per-scenario + plain-assertion style
/// rather than a markup snapshot, since these tests assert dynamic per-scenario values (average,
/// counts, pill flavour) that a byte-for-byte snapshot would obscure.
/// </summary>
public sealed class RecipeRatingDetailsTests(
    RecipeDetailFragmentFactory noRatingsFactory,
    RecipeDetailRatedMultiMemberFactory ratedMultiFactory,
    RecipeDetailRatedByOthersOnlyFactory ratedByOthersFactory,
    RecipeDetailSingleMemberRatedFactory singleMemberFactory)
    : IClassFixture<RecipeDetailFragmentFactory>,
      IClassFixture<RecipeDetailRatedMultiMemberFactory>,
      IClassFixture<RecipeDetailRatedByOthersOnlyFactory>,
      IClassFixture<RecipeDetailSingleMemberRatedFactory>
{
    // ── Initial render ───────────────────────────────────────────────────────

    /// <summary>
    /// No ratings anywhere (default fixture, single-member/no-directory household): the star-rating
    /// input renders with "Tap to rate" and value 0 — no household line at all (nothing to render inside
    /// the always-present #rd-rating-household wrapper).
    /// </summary>
    [Fact]
    public async Task No_Ratings_Renders_Input_With_Tap_To_Rate_Hint_And_No_Household_Line()
    {
        var html = await GetPageHtmlAsync(noRatingsFactory);

        Assert.Contains("starRatingInput({ value: 0,", html, StringComparison.Ordinal);
        Assert.Contains("id=\"rd-rating-household\"", html, StringComparison.Ordinal);
        // No household line content should be present (nothing rated, so ShowHouseholdLine is false).
        Assert.DoesNotContain("household average", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-rating-hh", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// I've rated 4 stars, Alex rated 5 (Sam hasn't) in a 3-member household: household line shows
    /// "household average · 2 of 3 rated" at 4.5, the warm --in pill flavour (my rating is included),
    /// and the popover lists all three members with "You" first and Sam's row reading "not rated".
    /// </summary>
    [Fact]
    public async Task Rated_By_Me_And_Alex_Shows_Household_Line_With_In_Flavour_And_Full_Breakdown()
    {
        var html = await GetPageHtmlAsync(ratedMultiFactory);

        Assert.Contains("starRatingInput({ value: 4,", html, StringComparison.Ordinal);
        Assert.Contains("household average &middot; 2 of 3 rated", html, StringComparison.Ordinal);
        Assert.Contains("rating-pill--in", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rating-pill--out", html, StringComparison.Ordinal);
        Assert.Contains(">4.5<", html, StringComparison.Ordinal);

        // Popover breakdown: "You" first, Sam's row reads "not rated".
        Assert.Contains("rating-pop-row--me", html, StringComparison.Ordinal);
        Assert.Contains(">You<", html, StringComparison.Ordinal);
        Assert.Contains(">Alex<", html, StringComparison.Ordinal);
        Assert.Contains(">Sam<", html, StringComparison.Ordinal);
        Assert.Contains("rating-pop-row__unrated\">not rated<", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only Alex has rated (I haven't): the household line renders with the grey-ghost --out pill
    /// flavour (my rating is NOT included in the average) and the input still shows value 0/"Tap to rate".
    /// </summary>
    [Fact]
    public async Task Rated_By_Others_Only_Shows_Out_Flavour_Pill()
    {
        var html = await GetPageHtmlAsync(ratedByOthersFactory);

        Assert.Contains("starRatingInput({ value: 0,", html, StringComparison.Ordinal);
        Assert.Contains("rating-pill--out", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rating-pill--in", html, StringComparison.Ordinal);
        Assert.Contains("household average &middot; 1 of 3 rated", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single-member household where that lone member has rated the recipe: the household line
    /// still stays suppressed (epic: "single-member household: no household line at all" — there's
    /// nothing to average even though RatedCount is 1), while the input itself reflects the rating.
    /// </summary>
    [Fact]
    public async Task Single_Member_Household_Never_Shows_Household_Line_Even_When_Rated()
    {
        var html = await GetPageHtmlAsync(singleMemberFactory);

        Assert.Contains("starRatingInput({ value: 5,", html, StringComparison.Ordinal);
        Assert.DoesNotContain("household average", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rd-rating-hh", html, StringComparison.Ordinal);
    }

    // ── OnPostRateAsync (rate / clear) ──────────────────────────────────────

    /// <summary>
    /// POST handler=Rate with stars=4 upserts a rating and returns the OOB household summary partial
    /// (hx-swap-oob, matching #rd-rating-household) — the household line appears for the FIRST time
    /// in this response even though the initial page had nothing inside the wrapper, proving the
    /// always-present-wrapper design gives the OOB swap a target on the very first rating.
    /// Uses a DEDICATED factory instance (not the shared <see cref="IClassFixture{TFixture}"/> one this
    /// class also reads from in <see cref="Rated_By_Others_Only_Shows_Out_Flavour_Pill"/>) — this test
    /// mutates the fake rating repository, and IClassFixture instances are shared across every test
    /// method in the class, so mutating a shared instance would make the other read-only tests order-
    /// dependent (mirrors the <c>using var factory = new ...()</c> pattern in RecipeInclusionTests.cs).
    /// </summary>
    [Fact]
    public async Task Post_Rate_New_Rating_In_Multi_Member_Household_Returns_Oob_Household_Summary()
    {
        using var factory = new RecipeDetailRatedByOthersOnlyFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());

        var token = await GetAntiforgeryTokenAsync(client, factory.RecipeId);

        var response = await client.PostAsync(
            $"/Recipes/{factory.RecipeId}?handler=Rate",
            new FormUrlEncodedContent([new("stars", "4"), new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"rd-rating-household\" hx-swap-oob=\"true\"", html, StringComparison.Ordinal);
        // Now BOTH Alex and I have rated → 2 of 3, average recomputed (5 + 4) / 2 = 4.5.
        Assert.Contains("household average &middot; 2 of 3 rated", html, StringComparison.Ordinal);
        Assert.Contains(">4.5<", html, StringComparison.Ordinal);
        Assert.Contains("rating-pill--in", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// POST handler=Rate with stars=0 clears an existing rating (the tap-current-again-to-clear
    /// contract star-rating.js's commit note calls out — a single handler URL, 0 mapped server-side to
    /// ClearRecipeRating). After clearing my rating in the 3-member/2-rated fixture, only Alex remains
    /// rated → the household line still shows (1 of 3) but flips to the --out (grey ghost) flavour.
    /// Dedicated factory instance — see the mutation-isolation note on the sibling test above.
    /// </summary>
    [Fact]
    public async Task Post_Rate_With_Zero_Clears_My_Rating()
    {
        using var factory = new RecipeDetailRatedMultiMemberFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());

        var token = await GetAntiforgeryTokenAsync(client, factory.RecipeId);

        var response = await client.PostAsync(
            $"/Recipes/{factory.RecipeId}?handler=Rate",
            new FormUrlEncodedContent([new("stars", "0"), new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("household average &middot; 1 of 3 rated", html, StringComparison.Ordinal);
        Assert.Contains("rating-pill--out", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rating-pill--in", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An out-of-range stars value (not 0-5) is rejected with 400 before touching the domain. Dedicated
    /// factory instance — this test posts against it, so it does not share state with the other tests
    /// reading <see cref="noRatingsFactory"/> (defensive consistency with the two mutation tests above;
    /// a rejected/no-op write wouldn't actually mutate state here, but keeping the pattern uniform avoids
    /// re-litigating the isolation question if this handler ever grows a side effect on the reject path).
    /// </summary>
    [Fact]
    public async Task Post_Rate_With_Out_Of_Range_Stars_Returns_BadRequest()
    {
        using var factory = new RecipeDetailFragmentFactory();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());

        var token = await GetAntiforgeryTokenAsync(client, factory.RecipeId);

        var response = await client.PostAsync(
            $"/Recipes/{factory.RecipeId}?handler=Rate",
            new FormUrlEncodedContent([new("stars", "6"), new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> GetPageHtmlAsync(RecipeDetailFragmentFactory factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Loads the Details page once to extract the antiforgery token (mirrors
    /// <c>ProductDetailMarkOpenedTests.GetAntiforgeryTokenAsync</c>) — the SAME client instance carries
    /// the antiforgery cookie WebApplicationFactory's default cookie handling attaches, so the follow-up
    /// POST on this client needs only the token form field, no manual cookie plumbing.
    /// </summary>
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid recipeId)
    {
        var html = await (await client.GetAsync($"/Recipes/{recipeId}")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }
}
