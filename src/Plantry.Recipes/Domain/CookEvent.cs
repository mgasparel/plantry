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

    /// <summary>
    /// Bare soft-ref (DM-3) to the MealPlanning <c>PlannedDish</c> this cook fulfilled, when the cook
    /// was launched from the meal plan (plantry-0eut). Null for a recipe-launched cook. This is the
    /// ONLY link between a cook and a plan — MealPlanning is never written to; the plan UI DERIVES
    /// cooked state by querying for a CookEvent with a matching PlannedDishId (no RecipeCookedEvent
    /// subscriber — ADR-014 guardrail, see the comment on RecipeCooked dispatch in CookRecipe.cs).
    /// </summary>
    public Guid? PlannedDishId { get; private set; }

    // ── Consume plan children (292b) ────────────────────────────────────────
    private readonly List<CookConsumeLine> _lines = [];

    /// <summary>
    /// The planned consume operations for this cook. Committed in
    /// <see cref="CookConsumeLineStatus.Pending"/> before any inventory call; each line
    /// transitions to <see cref="CookConsumeLineStatus.Applied"/> or
    /// <see cref="CookConsumeLineStatus.Shorted"/> after its inventory call returns.
    /// </summary>
    public IReadOnlyList<CookConsumeLine> ConsumeLines => _lines.AsReadOnly();

    // ── Produce plan children (yield-on-cook, plantry-854a) ──────────────────
    private readonly List<CookProduceLine> _produceLines = [];

    /// <summary>
    /// The planned yield-on-cook inventory ADDs for this cook (plantry-854a). Committed in
    /// <see cref="CookProduceLineStatus.Pending"/> in the same anchor-first commit as the consume
    /// lines, before any inventory call; each transitions to <see cref="CookProduceLineStatus.Applied"/>
    /// or <see cref="CookProduceLineStatus.Failed"/> after its inventory add returns. Empty for a cook
    /// of a recipe with no yield, or when the user stored nothing.
    /// </summary>
    public IReadOnlyList<CookProduceLine> ProduceLines => _produceLines.AsReadOnly();

    private CookEvent() { } // EF

    private CookEvent(
        CookEventId id,
        HouseholdId householdId,
        RecipeId recipeId,
        int servingsCooked,
        Guid cookedBy,
        DateTimeOffset cookedAt,
        Guid? plannedDishId)
    {
        Id = id;
        HouseholdId = householdId;
        RecipeId = recipeId;
        ServingsCooked = servingsCooked;
        CookedBy = cookedBy;
        CookedAt = cookedAt;
        PlannedDishId = plannedDishId;
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
    /// <param name="plannedDishId">
    /// Soft-ref (DM-3) to the plan dish this cook fulfilled, when launched from the meal plan
    /// (plantry-0eut); null for a direct recipe-launched cook (the default — existing callers unchanged).
    /// </param>
    /// <returns>The new <see cref="CookEvent"/>, or a <c>Recipes.InvalidServings</c> failure.</returns>
    public static Result<CookEvent> Record(
        RecipeId recipeId,
        HouseholdId householdId,
        int servingsCooked,
        Guid cookedBy,
        IClock clock,
        Guid? plannedDishId = null)
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
            clock.UtcNow,
            plannedDishId));
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
    /// <param name="sourceRecipeId">
    /// Cook-history provenance (D8): the recipe the ingredient physically belongs to. Pass the sub-recipe
    /// id for a line pulled in via an inclusion; leave <c>null</c> (default) for a direct line of the
    /// cooked recipe or an ad-hoc added product. Optional so existing direct-line call sites map 1:1.
    /// </param>
    /// <returns>The newly-added <see cref="CookConsumeLine"/>.</returns>
    public CookConsumeLine AddConsumeLine(
        Guid ingredientId, Guid productId, decimal quantity, Guid unitId, Guid? sourceRecipeId = null)
    {
        var line = new CookConsumeLine(
            CookConsumeLineId.New(),
            HouseholdId,
            Id,
            ingredientId,
            productId,
            quantity,
            unitId,
            sourceRecipeId);
        _lines.Add(line);
        return line;
    }

    /// <summary>
    /// Appends a planned yield-on-cook produce line in <see cref="CookProduceLineStatus.Pending"/> state
    /// (plantry-854a). Called by <c>CookRecipe</c> before the anchor <c>SaveChangesAsync</c> so the
    /// intended inventory ADD is durable before the produce call runs — reconcilable on interruption
    /// exactly like a consume line. The <c>sourceLineRef</c> idempotency token on the produce call is the
    /// returned line's own <see cref="CookProduceLine.Id"/>.
    /// </summary>
    /// <param name="productId">The yield product to store (soft-ref, DM-3).</param>
    /// <param name="quantity">The stored quantity (must be &gt; 0 — the caller skips a zero store).</param>
    /// <param name="unitId">Unit of <paramref name="quantity"/> — the recipe's declared yield unit.</param>
    /// <param name="expiryDate">User-supplied use-by date for the stored lot; null for none.</param>
    /// <returns>The newly-added <see cref="CookProduceLine"/>.</returns>
    public CookProduceLine AddProduceLine(
        Guid productId, decimal quantity, Guid unitId, DateOnly? expiryDate)
    {
        var line = new CookProduceLine(
            CookProduceLineId.New(),
            HouseholdId,
            Id,
            productId,
            quantity,
            unitId,
            expiryDate);
        _produceLines.Add(line);
        return line;
    }
}
