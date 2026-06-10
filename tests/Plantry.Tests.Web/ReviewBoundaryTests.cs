using System.Net;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Boundary assertions for the review route: it is gated by <c>[Authorize]</c> and tenant-scoped. These do not
/// snapshot markup — they assert the security envelope around the fragments.
/// </summary>
public sealed class ReviewBoundaryTests(ReviewFragmentFactory factory) : IClassFixture<ReviewFragmentFactory>
{
    private string ReviewUrl => $"/Intake/Review/{factory.SessionAId}";

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(ReviewUrl);

        // No household header → not authenticated → [Authorize] challenges. The test scheme issues a bare 401
        // (a cookie scheme would 302 to the login path); either is an "unauthenticated is blocked" boundary.
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"Expected 401/redirect for an unauthenticated request, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Household_that_owns_the_session_can_read_it()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Review &amp; confirm", body);
    }

    [Fact]
    public async Task Session_is_not_readable_by_another_household()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        // Household B authenticates, but the session belongs to household A.
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdBId.ToString());

        var response = await client.GetAsync(ReviewUrl);

        // The tenant-scoped repository returns null for a foreign household → the query maps to NotFound.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
