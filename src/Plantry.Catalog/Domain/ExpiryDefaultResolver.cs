namespace Plantry.Catalog.Domain;

/// <summary>
/// Resolves the expiry-default fallback chain (DM-11). Only the ambient/non-open case is
/// materialized here — freeze/thaw/open scenarios build on this in Slices 2–3.
/// </summary>
public static class ExpiryDefaultResolver
{
    /// <summary>The product's own default wins; falling back to its category's when unset.</summary>
    public static int? ResolveDefaultDueDays(Product product, Category? category) =>
        product.DefaultDueDays ?? category?.DefaultDueDays;
}
