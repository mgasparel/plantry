namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto the household's "expiring soon" horizon (owned by Inventory,
/// plantry-5yhd). Lets the Recipes context flag ingredients as "use soon" against the <b>same</b>
/// per-household threshold that drives Inventory's Today widget and <c>ExpiryTone.Soon</c> badge,
/// without coupling Recipes to Inventory's domain model or EF context (ADR-002). Defined here in
/// Recipes.Application and <b>implemented in Plantry.Web</b> over Inventory's
/// <c>IExpiringSoonHorizon</c>, so the Recipes project keeps its <c>→ SharedKernel only</c> dependency.
/// </summary>
public interface IExpiringSoonHorizonReader
{
    /// <summary>
    /// Returns the current household's "expiring soon" horizon in days, falling back to the Inventory
    /// default when unset. Used by <c>FulfillmentService</c> to compute <c>ExpiresWithinDays</c>.
    /// </summary>
    Task<int> GetDaysAsync(CancellationToken ct = default);
}
