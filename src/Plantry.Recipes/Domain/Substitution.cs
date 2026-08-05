using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Domain;

/// <summary>
/// Aggregate root — a household-scoped directed edge declaring that one catalog product can stand in
/// for another at a unit-bearing ratio (plantry-aqpa, epic "Ingredient substitutions"). Reading:
/// <see cref="SubstituteQuantity"/> of <see cref="SubstituteUnitId"/> of the substitute product satisfies
/// <see cref="TargetQuantity"/> of <see cref="TargetUnitId"/> of the target product — e.g. 100 g dried
/// chickpeas ≡ 260 g canned chickpeas.
/// <para>
/// Directed pairs are distinct edges — A→B and B→A may both exist, each carrying its own independently
/// authored ratio — unlike <c>ProductConversion</c>'s unordered-pair collapse (ADR-022 amendment), where a
/// single stored row is bidirectionally invertible and a reverse row would be a contradiction rather than
/// a distinct fact. UNIQUE (household_id, substitute_product_id, target_product_id) is the edge's identity
/// key; a duplicate directed pair on create replaces the existing edge (see
/// <see cref="Plantry.Recipes.Application.CreateSubstitution"/>) rather than being rejected.
/// </para>
/// <para>
/// No edit in v1 — delete + recreate is the repair path, matching <c>ProductConversion</c>'s
/// no-edit-UI precedent (there is no <c>UpdateConversion</c> application service either; ADR-022 §4
/// notes that if an edit path is ever added it must preserve the pairing invariant). No provenance
/// column, no AI fields — fully user-authored v1 (see epic).
/// </para>
/// </summary>
public sealed class Substitution : AggregateRoot<SubstitutionId>
{
    // Required by EF
    private Substitution() { }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>The ingredient the edge is written for — the recipe line this edge can help fulfil. Soft-ref (DM-3) to a Catalog product.</summary>
    public Guid TargetProductId { get; private set; }

    /// <summary>Amount, in <see cref="TargetUnitId"/>, of the target product an ingredient line calls for.</summary>
    public decimal TargetQuantity { get; private set; }

    /// <summary>Soft-ref (DM-3) to the target product's unit.</summary>
    public Guid TargetUnitId { get; private set; }

    /// <summary>The stand-in product. Soft-ref (DM-3) to a Catalog product.</summary>
    public Guid SubstituteProductId { get; private set; }

    /// <summary>Amount, in <see cref="SubstituteUnitId"/>, of the substitute product that satisfies <see cref="TargetQuantity"/>.</summary>
    public decimal SubstituteQuantity { get; private set; }

    /// <summary>Soft-ref (DM-3) to the substitute product's unit.</summary>
    public Guid SubstituteUnitId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Factory: creates a new directed substitution edge. Enforces the entity's invariants — no
    /// self-substitution, strictly positive quantities on both sides (both quantity+unit required on
    /// both sides; there is no unitless edge shape to omit a unit for).
    /// </summary>
    public static Substitution Create(
        HouseholdId householdId,
        Guid targetProductId, decimal targetQuantity, Guid targetUnitId,
        Guid substituteProductId, decimal substituteQuantity, Guid substituteUnitId,
        IClock clock)
    {
        Validate(targetProductId, targetQuantity, targetUnitId, substituteProductId, substituteQuantity, substituteUnitId);

        return new()
        {
            Id = SubstitutionId.New(),
            HouseholdId = householdId,
            TargetProductId = targetProductId,
            TargetQuantity = targetQuantity,
            TargetUnitId = targetUnitId,
            SubstituteProductId = substituteProductId,
            SubstituteQuantity = substituteQuantity,
            SubstituteUnitId = substituteUnitId,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Replaces this edge's ratio in place — the "duplicate directed pair on create" repair path
    /// (<see cref="Plantry.Recipes.Application.CreateSubstitution"/> upserts onto the existing row for the
    /// same (household, substitute, target) triple rather than erroring; a user re-asserting the pair is
    /// re-authoring the ratio "now", matching the spirit of <c>ProductConversion</c>'s user-confirmed-factor
    /// supersede rule). Re-validates every field — the caller may be replacing units, not just quantities.
    /// </summary>
    public void ReplaceRatio(
        decimal targetQuantity, Guid targetUnitId,
        decimal substituteQuantity, Guid substituteUnitId,
        IClock clock)
    {
        Validate(TargetProductId, targetQuantity, targetUnitId, SubstituteProductId, substituteQuantity, substituteUnitId);

        TargetQuantity = targetQuantity;
        TargetUnitId = targetUnitId;
        SubstituteQuantity = substituteQuantity;
        SubstituteUnitId = substituteUnitId;
        UpdatedAt = clock.UtcNow;
    }

    private static void Validate(
        Guid targetProductId, decimal targetQuantity, Guid targetUnitId,
        Guid substituteProductId, decimal substituteQuantity, Guid substituteUnitId)
    {
        if (targetProductId == Guid.Empty)
            throw new ArgumentException(
                "A substitution must reference a target product.", nameof(targetProductId));

        if (targetUnitId == Guid.Empty)
            throw new ArgumentException(
                "A substitution must specify a target unit.", nameof(targetUnitId));

        if (substituteProductId == Guid.Empty)
            throw new ArgumentException(
                "A substitution must reference a substitute product.", nameof(substituteProductId));

        if (substituteUnitId == Guid.Empty)
            throw new ArgumentException(
                "A substitution must specify a substitute unit.", nameof(substituteUnitId));

        if (substituteProductId == targetProductId)
            throw new ArgumentException(
                "A product cannot substitute for itself.", nameof(substituteProductId));

        if (targetQuantity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(targetQuantity), targetQuantity, "Target quantity must be strictly positive.");

        if (substituteQuantity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(substituteQuantity), substituteQuantity, "Substitute quantity must be strictly positive.");
    }
}
