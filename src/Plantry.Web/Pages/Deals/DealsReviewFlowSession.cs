using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The session-scoped facade over <see cref="IReviewFlowStateStore"/> for the guided review flow (q9zr.13).
/// It owns BOTH halves of the SO5.2 invariant — starting the ASP.NET Core session (so the session cookie is
/// issued and <see cref="Microsoft.AspNetCore.Http.ISession.Id"/> is stable across requests) AND deriving the
/// flow-store key from that session — and it is the ONLY way page code reaches the store. Because the key is
/// derived privately, right after the session is started, a caller can never obtain a key without the session
/// running: the invariant is structural, not a doc comment a new handler could forget.
///
/// <para><b>The bug this prevents (SO5.2).</b> If <see cref="FlowStoreKey"/>-style key derivation runs before
/// the session cookie is issued, the ASP.NET session id regenerates on every request and the flow-state key
/// rotates — silently losing the user's demotion / uncheck state. The former page model enforced "call
/// EnsureSessionStarted before FlowStoreKey" with a comment; this facade makes it impossible to violate.</para>
///
/// <para>Method names and signatures mirror <see cref="IReviewFlowStateStore"/> minus the <c>storeKey</c>
/// parameter, which this facade supplies. State is per household + browser session; see
/// <see cref="ReviewFlowState"/>.</para>
/// </summary>
public sealed class DealsReviewFlowSession(
    IReviewFlowStateStore store, IHttpContextAccessor http, ITenantContext tenant)
{
    /// <summary>The <c>_drf</c> sentinel written to force session-cookie issuance (the SO5.2 fix). Keep byte-identical.</summary>
    private static readonly byte[] SessionSentinel = [0x01];

    /// <summary>Returns the flow state for the current household/session, or <see cref="ReviewFlowState.Empty"/>.</summary>
    public async Task<ReviewFlowState> GetAsync(CancellationToken ct = default) =>
        await store.GetAsync(await StartedKeyAsync(ct), ct);

    /// <summary>Records whether one step-1 deal is currently unchecked, persisting it across step round-trips / refresh.</summary>
    public async Task SetUncheckedAsync(Guid dealId, bool isUnchecked, CancellationToken ct = default) =>
        await store.SetUncheckedAsync(await StartedKeyAsync(ct), dealId, isUnchecked, ct);

    /// <summary>
    /// Commits a step-1 confirmation: demotes the unchecked Highs and clears the given ids from the unchecked set.
    /// </summary>
    public async Task CommitAsync(
        IEnumerable<Guid> demote, IEnumerable<Guid> clearUnchecked, CancellationToken ct = default) =>
        await store.CommitAsync(await StartedKeyAsync(ct), demote, clearUnchecked, ct);

    /// <summary>
    /// Starts the session (issuing the cookie so the id is stable) and derives the flow-store key
    /// <c>deals-review-flow_{householdId:N}_{sessionId}</c>. The two steps are inseparable here — that is the point.
    /// Writing the <c>_drf</c> sentinel byte when absent forces cookie issuance (the SO5.2 fix); the key format
    /// must stay byte-identical so live sessions do not lose their flow state on deploy.
    /// </summary>
    private async Task<string> StartedKeyAsync(CancellationToken ct)
    {
        var session = (http.HttpContext
            ?? throw new InvalidOperationException("No active HttpContext — the review flow session requires a request."))
            .Session;

        await session.LoadAsync(ct);
        if (!session.TryGetValue("_drf", out _))
            session.Set("_drf", SessionSentinel);

        return $"deals-review-flow_{tenant.HouseholdId ?? Guid.Empty:N}_{session.Id}";
    }
}
