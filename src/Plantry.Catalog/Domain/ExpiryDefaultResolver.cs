namespace Plantry.Catalog.Domain;

/// <summary>
/// Resolves the expiry-default fallback chain (DM-11). <see cref="ResolveDefaultDueDays"/> covers the
/// ambient/non-open printed-date case; <see cref="ResolveDefaultDueDaysAfterOpening"/> covers the
/// after-opening case (plantry-1le6). Freeze/thaw (plantry-6owm) resolve off the product first, then
/// the household-wide default (plantry-hh1f) — see <see cref="ResolveDefaultDueDaysAfterFreezing"/>.
/// </summary>
public static class ExpiryDefaultResolver
{
    /// <summary>The product's own default wins; falling back to its category's when unset.</summary>
    public static int? ResolveDefaultDueDays(Product product, Category? category) =>
        product.DefaultDueDays ?? category?.DefaultDueDays;

    /// <summary>
    /// The product's own after-opening default (DM-11 rule 1, plantry-1le6). Unlike
    /// <see cref="ResolveDefaultDueDays"/>, <see cref="Category"/> carries no per-transition due-days
    /// field of its own (only the plain <see cref="Category.DefaultDueDays"/> for the printed date) —
    /// there is no category level to fall back to yet, so this resolves from the product alone. Kept
    /// as its own method, mirroring <see cref="ResolveDefaultDueDays"/>'s shape, so a future
    /// category-level after-opening field slots in here without touching callers.
    /// </summary>
    public static int? ResolveDefaultDueDaysAfterOpening(Product product) => product.DefaultDueDaysAfterOpening;

    /// <summary>
    /// The product's own after-freezing default wins (plantry-6owm rule 3); when unset, falls back to
    /// <paramref name="householdDefault"/> — the household-wide default (plantry-hh1f) resolved by the
    /// caller through the <c>IHouseholdExpiryDefaultsReader</c> anti-corruption port onto Identity's
    /// <c>Household.DefaultDueDaysAfterFreezing</c>. Unlike <see cref="ResolveDefaultDueDays"/>,
    /// <see cref="Category"/> carries no per-transition due-days field to fall back to — the household
    /// default is the backstop instead. Always resolves to a concrete value (never null): an
    /// auto-created product with neither a category nor its own per-product freeze/thaw fields set
    /// (e.g. a cooked-leftovers product, plantry-hh1f's original report) now still recomputes its
    /// expiry on freeze instead of the transfer silently leaving it unchanged.
    /// </summary>
    public static int ResolveDefaultDueDaysAfterFreezing(Product product, int householdDefault) =>
        product.DefaultDueDaysAfterFreezing ?? householdDefault;

    /// <summary>
    /// The product's own after-thawing default (plantry-6owm rule 3), falling back to
    /// <paramref name="householdDefault"/>. Mirrors <see cref="ResolveDefaultDueDaysAfterFreezing"/>.
    /// </summary>
    public static int ResolveDefaultDueDaysAfterThawing(Product product, int householdDefault) =>
        product.DefaultDueDaysAfterThawing ?? householdDefault;
}
