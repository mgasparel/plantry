namespace Plantry.Web.Pages.Deals;

/// <summary>
/// The presentation-tier state of the guided review flow for one household/session (q9zr.13). Both sets are
/// deal ids scoped by the <c>storeKey</c> the caller builds (<c>{household}_{session}</c>):
/// <list type="bullet">
///   <item><see cref="Demoted"/> — Highs the user unchecked AND committed in step 1; they now live in step 2's
///     judgement-call pool. Drives step membership so a refresh keeps a demoted deal out of step 1.</item>
///   <item><see cref="Unchecked"/> — Highs currently unchecked in the step-1 checklist but not yet committed.
///     Drives each checkbox's initial state so jumping away and back (or refreshing) does not re-check them
///     (the real prototype bug the adopted design fixed).</item>
/// </list>
/// This is deliberately NOT domain state — "demoted" is which step shows a deal, never a fact about the Deal
/// aggregate (no column is added). See <see cref="IReviewFlowStateStore"/>.
/// </summary>
public sealed record ReviewFlowState(IReadOnlySet<Guid> Demoted, IReadOnlySet<Guid> Unchecked)
{
    /// <summary>The empty state — nothing demoted, nothing unchecked.</summary>
    public static readonly ReviewFlowState Empty =
        new(new HashSet<Guid>(), new HashSet<Guid>());
}

/// <summary>
/// A transient, session-keyed store for the guided-flow presentation state (demoted + unchecked deal ids). Keyed
/// by <c>{householdId:N}_{sessionId}</c> so state is per household/session and expires on its own — the same
/// vetted pattern as <c>IPendingProposalStore</c> (backed by <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>).
/// </summary>
public interface IReviewFlowStateStore
{
    /// <summary>Returns the flow state for <paramref name="storeKey"/>, or <see cref="ReviewFlowState.Empty"/> if none.</summary>
    Task<ReviewFlowState> GetAsync(string storeKey, CancellationToken ct = default);

    /// <summary>
    /// Records whether one step-1 deal is currently unchecked, persisting it across step round-trips / refresh.
    /// Checking a previously-unchecked deal removes it from the unchecked set.
    /// </summary>
    Task SetUncheckedAsync(string storeKey, Guid dealId, bool isUnchecked, CancellationToken ct = default);

    /// <summary>
    /// Commits a step-1 confirmation: adds <paramref name="demote"/> (the unchecked Highs) to the demoted set,
    /// and clears every id in <paramref name="clearUnchecked"/> from the unchecked set (the confirmed Highs left
    /// the queue; the demoted ones now live in step 2 with no checkbox). Idempotent.
    /// </summary>
    Task CommitAsync(
        string storeKey, IEnumerable<Guid> demote, IEnumerable<Guid> clearUnchecked, CancellationToken ct = default);
}
