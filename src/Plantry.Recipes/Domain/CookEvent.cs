using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Append-only root — an immutable record that a recipe was cooked (recipes-domain-model.md §1, C3 / R8).
/// Never updated or deleted, so it carries no <c>updated_at</c>. Its id is the <c>source_ref</c> Inventory's
/// <c>Consume</c> stamps on the resulting journal rows. The <c>recipe_id</c> FK is <c>ON DELETE RESTRICT</c> —
/// safe because a cooked recipe is soft-deleted, never physically removed. P2-0 step maps the shape only;
/// the <c>CookRecipe</c> flow lands later.
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

    private CookEvent() { } // EF
}
