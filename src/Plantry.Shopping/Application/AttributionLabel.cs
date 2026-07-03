namespace Plantry.Shopping.Application;

/// <summary>
/// Structural kind of a shopping-item <see cref="AttributionLabel"/> — mirrors the domain
/// <see cref="Plantry.Shopping.Domain.ItemSource"/> enum 1:1 (TRIAGE DECISION 2026-07-03).
///
/// <para>The UI keys presentation (e.g. the recipe icon) off this <b>structural</b> kind, never off
/// the label's display text. Only <see cref="Recipe"/> drives an icon today; <see cref="MealPlan"/>
/// and <see cref="Deal"/> are inert cases that emit no label until their attribution resolvers land
/// (tracked separately — see plantry-jwyb).</para>
/// </summary>
public enum AttributionKind
{
    /// <summary>Item was added manually by a household member ("added by you").</summary>
    Manual,

    /// <summary>Item originates from a recipe contribution ("for {RecipeName}"). Drives the recipe icon.</summary>
    Recipe,

    /// <summary>Item originates from a meal-plan contribution. Inert until a resolver lands (no label emitted).</summary>
    MealPlan,

    /// <summary>Item originates from a deal contribution. Inert until a resolver lands (no label emitted).</summary>
    Deal,
}

/// <summary>
/// A resolved attribution descriptor for one shopping-item contribution: the structural
/// <see cref="Kind"/> the UI keys presentation off, plus the human-readable <see cref="Text"/>
/// rendered on the source sub-line.
///
/// <para>Replaces the earlier display-string list (<c>IReadOnlyList&lt;string&gt;</c>) so that
/// presentation semantics — chiefly whether to render the recipe icon — derive from a typed
/// discriminator set structurally from <see cref="Plantry.Shopping.Domain.ItemSource"/>, not from
/// sniffing the display wording. A reword or localization of <see cref="Text"/> can no longer
/// silently change which icon renders.</para>
/// </summary>
/// <param name="Kind">Structural provenance of the label — drives presentation (e.g. the recipe icon).</param>
/// <param name="Text">Human-readable display text (e.g. "for Roast Dinner", "added by you").</param>
public sealed record AttributionLabel(AttributionKind Kind, string Text);
