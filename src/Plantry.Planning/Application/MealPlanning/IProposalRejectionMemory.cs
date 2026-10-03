using Plantry.Planning.Domain;

namespace Plantry.Planning.Application;

/// <summary>
/// Session-keyed memory of recipes the user has already seen proposed for a cell and passed over
/// (rejected, regenerated away, or discarded). The planner is deterministic, so without this a
/// Regenerate re-picks the identical recipe. Shares the pending-proposal store key and lifetime:
/// <c>{householdId}_{weekStart:yyyyMMdd}_{sessionId}</c>.
/// </summary>
public interface IProposalRejectionMemory
{
    /// <summary>Returns the rejected recipe IDs per cell (keyed by <see cref="CellKey"/>); empty if none.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> GetAsync(string storeKey, CancellationToken ct = default);

    /// <summary>Remembers the recipes of the given proposals as rejected for their cells.</summary>
    Task RememberAsync(string storeKey, IEnumerable<ProposedMeal> rejected, CancellationToken ct = default);

    /// <summary>Stable per-cell key shared by callers and the planner.</summary>
    static string CellKey(DateOnly date, MealSlotId slotId) => $"{date:yyyy-MM-dd}_{slotId.Value:N}";
}
