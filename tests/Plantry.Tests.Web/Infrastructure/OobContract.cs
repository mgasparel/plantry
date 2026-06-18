using Xunit;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Shared assertion for the ADR-013 out-of-band response contract: a mutation handler returns the
/// changed element plus a fresh fragment for EVERY page-level derived-view projection — never a
/// full-region repaint. The page's aggregates stay in lock-step with the change because the response
/// <em>carries</em> them, not because something repaints the whole region and hides the drift.
///
/// Born feature-local in the intake review form (<c>ReviewBoundaryTests</c>). Promoted here after
/// plantry-6si showed the pattern — living only as ADR-013 prose plus an Intake-only test — failed to
/// transfer to the meal planner: the worker, the reviewer, and a three-pass critic all missed it.
/// Both features now assert the contract through this one primitive, so "every mutation carries its
/// projection bundle" is enforced structure rather than per-feature memory. See ADR-013.
/// </summary>
public static class OobContract
{
    /// <summary>
    /// Asserts the response body carries a fresh element for each derived-view projection id
    /// (e.g. <c>plan-rail</c> for the meal planner; <c>rev-chips</c>/<c>rev-progress</c>/
    /// <c>commit-bar</c>/<c>rcpt-total</c> for intake review). Mechanism-agnostic on purpose: the
    /// projection may arrive inline (a full-region swap) or out-of-band (a targeted swap) — what the
    /// contract forbids is a bare changed-element response that drops the projection, which is exactly
    /// the bug class this primitive exists to catch.
    /// </summary>
    public static void AssertCarriesProjections(string body, params string[] projectionIds)
    {
        foreach (var id in projectionIds)
        {
            Assert.True(
                body.Contains($"id=\"{id}\"", StringComparison.Ordinal),
                $"ADR-013 OOB-contract violation: the mutation response is missing the derived-view " +
                $"projection #{id}. Every mutation handler must return its projection bundle so page-level " +
                $"derived state never drifts from the change. A response that swaps only the changed element " +
                $"and drops #{id} is the staleness bug this contract exists to prevent.");
        }
    }

    /// <summary>
    /// Asserts the response retargets htmx to the changed element via the <c>HX-Retarget</c> header
    /// (the server-driven row-swap mechanism the intake review form uses). Mechanism-specific:
    /// features that perform the primary swap client-side (e.g. the meal planner's <c>htmx.swap</c> on a
    /// cell id) will not set this header and should assert the projection bundle only.
    /// </summary>
    public static void AssertRetargets(HttpResponseMessage response, string target)
    {
        Assert.True(
            response.Headers.TryGetValues("HX-Retarget", out var values) && values.Single() == target,
            $"ADR-013 OOB-contract: expected HX-Retarget '{target}' on the response.");
    }

    /// <summary>
    /// Asserts the body does NOT carry a full-region innerHTML repaint (the retired pre-ADR-013
    /// approach). A successful mutation must deliver targeted projection fragments, never repaint the
    /// whole region — that coupling is what made local edits impossible to reason about.
    /// </summary>
    public static void AssertNoFullRepaint(string body, string regionId)
    {
        Assert.False(
            body.Contains($"id=\"{regionId}\"", StringComparison.Ordinal),
            $"ADR-013 OOB-contract violation: the response repaints the whole #{regionId} region instead " +
            $"of delivering targeted projection fragments.");
    }
}
