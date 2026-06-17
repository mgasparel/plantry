using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Application;

/// <summary>
/// Session-keyed, transient store for AI-proposed meals awaiting user acceptance or discard.
/// AI output is staged here (validated but not committed to the MealPlan aggregate) until the user
/// acts. The store is keyed by <c>{householdId}_{weekStart:yyyyMMdd}_{sessionId}</c>.
/// Implemented over IDistributedCache (or IMemoryCache).
/// </summary>
public interface IPendingProposalStore
{
    /// <summary>Returns all pending proposals for the given store key, or an empty list if none.</summary>
    Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default);

    /// <summary>Stores/replaces all proposals for the given store key.</summary>
    Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default);

    /// <summary>Removes the proposal for a specific cell. No-op if not present.</summary>
    Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default);

    /// <summary>Clears all proposals for the given store key.</summary>
    Task ClearAsync(string storeKey, CancellationToken ct = default);
}
