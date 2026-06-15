using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Append-only root — an immutable record that a recipe was cooked (recipes-domain-model.md §1, C3 / R8).
/// Never updated or deleted, so it carries no <c>updated_at</c>. Its id is the <c>source_ref</c> Inventory's
/// <c>Consume</c> stamps on the resulting journal rows. The <c>recipe_id</c> FK is <c>ON DELETE RESTRICT</c> —
/// safe because a cooked recipe is soft-deleted, never physically removed.
/// <para>
/// Created via <see cref="Record"/>. Child <see cref="ConsumeLines"/> are added via
/// <see cref="AddConsumeLine"/> during the anchor-first persist step (292b): all lines start
/// <see cref="CookConsumeLineStatus.Pending"/> and are committed before any inventory call runs.
/// After each consume the caller marks the line <see cref="CookConsumeLineStatus.Applied"/> or
/// <see cref="CookConsumeLineStatus.Shorted"/> via the line's own mutators, then commits again.
/// </para>
/// </summary>
public sealed class CookEvent : AggregateRoot<CookEventId>
{
    public HouseholdId HouseholdId { get; private set; }
    public RecipeId RecipeId { get; private set; }

    /// <summary>The materialized <c>ServingsScale × default</c> at cook time; <c>CHECK (>= 1)</c> (R2).</summary>
    public int ServingsCooked { get; private set; }

    /// <summary>Soft ref → identity user (O2); captured at write time — append-only, so unrecoverable if missed.</summary>
    public Guid CookedBy { get; private set; }
    public DateTimeOffset CookedAt { get; private set; }

    // ── Consume plan children (292b) ────────────────────────────────────────
    private readonly List<CookConsumeLine> _lines = [];

    /// <summary>
    /// The planned consume operations for this cook. Committed in
    /// <see cref="CookConsumeLineStatus.Pending"/> before any inventory call; each line
    /// transitions to <see cref="CookConsumeLineStatus.Applied"/> or
    /// <see cref="CookConsumeLineStatus.Shorted"/> after its inventory call returns.
    /// </summary>
    public IReadOnlyList<CookConsumeLine> ConsumeLines => _lines.AsReadOnly();

    private CookEvent() { } // EF

    private CookEvent(
        CookEventId id,
        HouseholdId householdId,
        RecipeId recipeId,
        int servingsCooked,
        Guid cookedBy,
        DateTimeOffset cookedAt)
    {
        Id = id;
        HouseholdId = householdId;
        RecipeId = recipeId;
        ServingsCooked = servingsCooked;
        CookedBy = cookedBy;
        CookedAt = cookedAt;
    }

    /// <summary>
    /// The single creation point for a cook record (R8 — append-only, no update or delete).
    /// <paramref name="servingsCooked"/> must be &gt;= 1 (R2 / CHECK on the DB column).
    /// No domain event is raised here; <c>RecipeCooked</c> is emitted by the
    /// <c>CookRecipe</c> application service (P2-3c) which orchestrates the full flow.
    /// </summary>
    /// <param name="recipeId">The recipe that was cooked.</param>
    /// <param name="householdId">The tenant this cook event belongs to.</param>
    /// <param name="servingsCooked">Number of servings produced; must be &gt;= 1.</param>
    /// <param name="cookedBy">Identity of the user who initiated the cook.</param>
    /// <param name="clock">Wall-clock source for <see cref="CookedAt"/>.</param>
    /// <returns>The new <see cref="CookEvent"/>, or a <c>Recipes.InvalidServings</c> failure.</returns>
    public static Result<CookEvent> Record(
        RecipeId recipeId,
        HouseholdId householdId,
        int servingsCooked,
        Guid cookedBy,
        IClock clock)
    {
        if (servingsCooked < 1)
            return Result<CookEvent>.Failure(Error.Custom("Recipes.InvalidServings",
                "Servings cooked must be at least 1."));

        return Result<CookEvent>.Success(new CookEvent(
            CookEventId.New(),
            householdId,
            recipeId,
            servingsCooked,
            cookedBy,
            clock.UtcNow));
    }

    /// <summary>
    /// Appends a planned consume line in <see cref="CookConsumeLineStatus.Pending"/> state
    /// (292b anchor-first step). Called by <c>CookRecipe</c> before the first
    /// <c>SaveChangesAsync</c> — all lines are committed together with the root.
    /// </summary>
    /// <param name="ingredientId">
    /// The ingredient this line resolves (soft-ref, DM-3). The <c>sourceLineRef</c> idempotency
    /// token on the Inventory consume call is the returned line's own <see cref="CookConsumeLine.Id"/>
    /// — a per-cook-unique Guid — NOT this ingredient id (plantry-fks).
    /// </param>
    /// <param name="productId">The resolved product to consume.</param>
    /// <param name="quantity">Scaled quantity to consume.</param>
    /// <param name="unitId">Unit of <paramref name="quantity"/>.</param>
    /// <returns>The newly-added <see cref="CookConsumeLine"/>.</returns>
    public CookConsumeLine AddConsumeLine(Guid ingredientId, Guid productId, decimal quantity, Guid unitId)
    {
        var line = new CookConsumeLine(
            CookConsumeLineId.New(),
            HouseholdId,
            Id,
            ingredientId,
            productId,
            quantity,
            unitId);
        _lines.Add(line);
        return line;
    }
}
