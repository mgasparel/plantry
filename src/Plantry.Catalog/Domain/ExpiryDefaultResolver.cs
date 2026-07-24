namespace Plantry.Catalog.Domain;

/// <summary>
/// Resolves the expiry-default fallback chain (DM-11). <see cref="ResolveDefaultDueDays"/> covers the
/// ambient/non-open printed-date case; <see cref="ResolveDefaultDueDaysAfterOpening"/> covers the
/// after-opening case (plantry-1le6). Freeze/thaw (plantry-6owm) resolve directly off the product for
/// the same reason documented there.
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
    /// The product's own after-freezing default (plantry-6owm rule 3). Resolves from the product alone
    /// for the same reason as <see cref="ResolveDefaultDueDaysAfterOpening"/> — <see cref="Category"/>
    /// has no per-transition due-days field to fall back to.
    /// </summary>
    public static int? ResolveDefaultDueDaysAfterFreezing(Product product) => product.DefaultDueDaysAfterFreezing;

    /// <summary>
    /// The product's own after-thawing default (plantry-6owm rule 3). Mirrors
    /// <see cref="ResolveDefaultDueDaysAfterFreezing"/>.
    /// </summary>
    public static int? ResolveDefaultDueDaysAfterThawing(Product product) => product.DefaultDueDaysAfterThawing;
}
