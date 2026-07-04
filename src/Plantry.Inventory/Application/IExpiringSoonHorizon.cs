namespace Plantry.Inventory.Application;

/// <summary>
/// The single source of truth for a household's "expiring soon" horizon (in days). Reads the
/// per-household setting, falling back to <see cref="Domain.HouseholdInventorySettings.DefaultExpiringSoonDays"/>
/// when none is configured. Consumed inside Inventory by <see cref="InventoryQueryService"/> (the Today
/// expiring-soon widget and the <c>ExpiryTone.Soon</c> badge) and, across the context boundary, by the
/// Recipes browse "use soon" filter through a thin Web-side adapter — so every "expiring soon" surface
/// resolves the same value (plantry-5yhd).
/// </summary>
public interface IExpiringSoonHorizon
{
    /// <summary>
    /// Returns the current household's configured horizon in days, or the default when unset or when
    /// there is no household in context.
    /// </summary>
    Task<int> GetDaysAsync(CancellationToken ct = default);
}
