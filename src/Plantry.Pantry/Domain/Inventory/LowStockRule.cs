using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Pantry.Domain;

/// <summary>
/// The per-household, per-product low-stock threshold (plantry-oh27.1) — the "running low at"
/// quantity, keyed by <see cref="ProductId"/> alone (no leaf/parent distinction at the domain
/// level, so a rule can target either). Replaces the old <c>ProductStock.LowStockThreshold</c>,
/// which could only ever be set on a leaf (a parent can never hold a <see cref="ProductStock"/>
/// row) — this record gives the threshold a home independent of whether stock exists yet.
///
/// <para>
/// A cleared / null / zero threshold is represented by the <b>absence</b> of a row, not a null or
/// zero field — <see cref="Threshold"/> is always strictly positive. Callers that today treat
/// "null or zero" as "no threshold" get the same user-facing behaviour by deleting the rule via
/// <see cref="ILowStockRuleRepository.RemoveAsync"/> instead of writing a null/zero value.
/// </para>
/// </summary>
public sealed class LowStockRule
{
    // Required by EF
    private LowStockRule() { }

    private LowStockRule(HouseholdId householdId, Guid productId, decimal threshold, DateTimeOffset now)
    {
        HouseholdId = householdId;
        ProductId = productId;
        Threshold = threshold;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Soft reference into <c>catalog.products</c> (DM-3, no enforced cross-context FK) — may be
    /// a leaf or a parent product; this bead adds no leaf/parent branching, only the record itself.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Always strictly greater than zero — "no rule" is modelled as the absence of a row.</summary>
    public decimal Threshold { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a new rule. Throws if <paramref name="threshold"/> is not strictly positive —
    /// callers wanting "no threshold" must not create a rule at all (or must remove an existing one).</summary>
    public static LowStockRule Create(HouseholdId householdId, Guid productId, decimal threshold, IClock clock)
    {
        if (threshold <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(threshold), threshold,
                "Low stock threshold must be greater than zero; a cleared threshold is represented by the absence of a rule.");
        return new LowStockRule(householdId, productId, threshold, clock.UtcNow);
    }

    /// <summary>Updates the threshold on an existing rule and bumps <see cref="UpdatedAt"/>, matching the
    /// house style of <c>ProductStock.AddStock</c>/<c>Consume</c>. Throws under the same guard as
    /// <see cref="Create"/> — clearing a threshold is done by removing the rule, not by setting it to
    /// zero/negative.</summary>
    public void SetThreshold(decimal threshold, IClock clock)
    {
        if (threshold <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(threshold), threshold,
                "Low stock threshold must be greater than zero; a cleared threshold is represented by the absence of a rule.");
        Threshold = threshold;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Same semantics as the retired <c>ProductStock.IsRunningLow</c>: true when on-hand is at or
    /// below the threshold.</summary>
    public bool IsRunningLow(decimal onHand) => onHand <= Threshold;
}
