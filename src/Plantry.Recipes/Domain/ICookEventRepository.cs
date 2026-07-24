namespace Plantry.Recipes.Domain;

/// <summary>
/// Persistence port for the append-only <see cref="CookEvent"/> aggregate (R8).
/// Implemented in Plantry.Recipes.Infrastructure.
/// </summary>
public interface ICookEventRepository
{
    /// <summary>Stages a new <see cref="CookEvent"/> for insertion. Call <see cref="SaveChangesAsync"/> to commit.</summary>
    Task AddAsync(CookEvent cookEvent, CancellationToken ct = default);

    /// <summary>
    /// Returns the cook history for a single recipe, ordered by <c>cooked_at desc</c> (most recent first).
    /// Feeds the future cook-history read model on the recipe detail page.
    /// </summary>
    Task<IReadOnlyList<CookEvent>> ListByRecipeAsync(RecipeId recipeId, CancellationToken ct = default);

    /// <summary>
    /// Returns all <see cref="CookEvent"/> aggregates for the household that have at least one
    /// <see cref="CookConsumeLine"/> in <see cref="CookConsumeLineStatus.Pending"/> state, with
    /// their <see cref="CookEvent.ConsumeLines"/> eagerly loaded.
    /// Used by <c>ReconcilePendingCooks</c> (292c) to find interrupted cooks for re-driving.
    /// </summary>
    Task<IReadOnlyList<CookEvent>> ListWithPendingLinesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all <see cref="CookEvent"/> aggregates for the household that have at least one
    /// <see cref="CookConsumeLine"/> in <see cref="CookConsumeLineStatus.DeferredUnitGap"/> state whose
    /// <see cref="CookConsumeLine.ProductId"/> is in <paramref name="productIds"/>, with their
    /// <see cref="CookEvent.ConsumeLines"/> eagerly loaded. Used by <c>ApplyDeferredUnitGaps</c> (retro-
    /// apply once a conversion lands) and <c>VoidDeferredUnitGapLines</c> (void on an absolute
    /// observation) — plantry-qll2.6. Empty <paramref name="productIds"/> returns nothing.
    /// </summary>
    Task<IReadOnlyList<CookEvent>> ListWithDeferredUnitGapLinesForProductsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Lightweight name-free projection — the recipe id each of the given cook events belongs to (receipt-
    /// intake-history.md H4, the Cook side of the pantry-history provenance chip). Ids not found (deleted
    /// or foreign-household, filtered by the RLS query filter) are silently omitted, mirroring
    /// <see cref="IRecipeRepository.GetRecipeNamesByIdAsync"/>'s existence semantics. Empty input
    /// returns an empty result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, RecipeId>> GetRecipeIdsByCookEventIdsAsync(
        IReadOnlyCollection<Guid> cookEventIds, CancellationToken ct = default);

    /// <summary>
    /// Latest <c>CookedAt</c> per <see cref="CookEvent.PlannedDishId"/> among the given plan dish ids
    /// (plantry-0eut — the MealPlanning cook-status read port's recipe-dish leg). A plan dish never
    /// cooked, or cooked before a foreign-household filter removed it, is absent from the result. A
    /// dish re-cooked more than once (re-launching the Cook page from an already-done card) resolves to
    /// its most recent cook. Empty input returns an empty result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLatestCookedAtByPlannedDishIdsAsync(
        IReadOnlyCollection<Guid> plannedDishIds, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
