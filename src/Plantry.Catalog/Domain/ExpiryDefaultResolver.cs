using Plantry.SharedKernel.Domain;

namespace Plantry.Catalog.Domain;

/// <summary>
/// Resolves Catalog expiry policies. Freeze/thaw resolve the product's live Never rule first,
/// then its day-count override, then the household-wide day default.
/// </summary>
public static class ExpiryDefaultResolver
{
    /// <summary>The product's own default wins; falling back to its category's when unset.</summary>
    public static int? ResolveDefaultDueDays(Product product, Category? category) =>
        product.DefaultDueDays ?? category?.DefaultDueDays;

    /// <summary>
    /// The product's own after-opening default. Category carries no per-transition after-opening
    /// field, so this resolves from the product alone.
    /// </summary>
    public static int? ResolveDefaultDueDaysAfterOpening(Product product) => product.DefaultDueDaysAfterOpening;

    /// <summary>
    /// Resolves the normal after-freezing policy exhaustively. A local true Never decision wins;
    /// otherwise a variant with a null local decision follows its parent's Never flag live. Once the
    /// effective Never flag is false, the product day override wins and the household day default is
    /// the final fallback. A root with a null local decision is not-Never.
    /// </summary>
    public static ExpiryTransitionPolicy ResolveAfterFreezing(
        Product product, Product? parent, int householdDefault) =>
        Resolve(
            product,
            parent,
            product.NeverExpiresAfterFreezing,
            product.DefaultDueDaysAfterFreezing,
            householdDefault,
            parentFlagSelector: p => p.NeverExpiresAfterFreezing);

    /// <summary>Resolves the normal after-thawing policy with the same precedence.</summary>
    public static ExpiryTransitionPolicy ResolveAfterThawing(
        Product product, Product? parent, int householdDefault) =>
        Resolve(
            product,
            parent,
            product.NeverExpiresAfterThawing,
            product.DefaultDueDaysAfterThawing,
            householdDefault,
            parentFlagSelector: p => p.NeverExpiresAfterThawing);

    private static ExpiryTransitionPolicy Resolve(
        Product product,
        Product? parent,
        bool? localNever,
        int? productDays,
        int householdDefault,
        Func<Product, bool?> parentFlagSelector)
    {
        var effectiveNever = localNever
            ?? (product.ParentProductId is not null
                ? parent is not null ? parentFlagSelector(parent) : null
                : null)
            ?? false;

        if (effectiveNever)
            return new ExpiryTransitionPolicy.Never();

        return new ExpiryTransitionPolicy.Days(productDays ?? householdDefault);
    }
}
