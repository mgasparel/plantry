using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plantry.Web.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Test authentication scheme for the L4 WebApplicationFactory harness. The real app authenticates via the
/// Identity application cookie; here we stub it so a request arrives already authenticated as a chosen
/// household — the production <see cref="RlsMiddleware"/> then reads the <c>household_id</c> claim and arms the
/// scoped <c>TenantContext</c> exactly as it does in prod, so the household-scoping boundary is exercised for
/// real (not bypassed).
///
/// A request is authenticated iff it carries the <see cref="HouseholdHeader"/> header; its value becomes the
/// household_id claim. No header → no principal → the <c>[Authorize]</c> page redirects to the login path, which
/// is the unauthenticated-boundary assertion.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Per-request header carrying the household id to authenticate as.</summary>
    public const string HouseholdHeader = "X-Test-Household";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HouseholdHeader, out var values) || values.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var householdId = values.ToString();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-0000000000aa"),
            new Claim(HouseholdIdClaims.ClaimType, householdId),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
