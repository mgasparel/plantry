using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

/// <summary>
/// L1 unit tests for <see cref="ExpiryDefaultResolver"/> — the start of the DM-11 fallback
/// chain (product default wins; category default is the backstop).
/// </summary>
public sealed class ExpiryDefaultResolverTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static Product NewProduct(int? defaultDueDays) =>
        Product.Create(HouseholdId, "Milk", UnitId.New(), Clock) is var product
            ? WithDueDays(product, defaultDueDays)
            : throw new InvalidOperationException();

    private static Product WithDueDays(Product product, int? dueDays)
    {
        product.SetExpiryDefaults(dueDays, null, null, null, Clock);
        return product;
    }

    [Fact]
    public void ProductDefault_Wins_When_Set()
    {
        var product = NewProduct(defaultDueDays: 5);
        var category = Category.Create(HouseholdId, "Dairy", defaultDueDays: 10);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDays(product, category);

        Assert.Equal(5, resolved);
    }

    [Fact]
    public void CategoryDefault_Used_When_Product_Default_Is_Null()
    {
        var product = NewProduct(defaultDueDays: null);
        var category = Category.Create(HouseholdId, "Dairy", defaultDueDays: 10);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDays(product, category);

        Assert.Equal(10, resolved);
    }

    [Fact]
    public void Returns_Null_When_Neither_Product_Nor_Category_Has_A_Default()
    {
        var product = NewProduct(defaultDueDays: null);
        var category = Category.Create(HouseholdId, "Dairy");

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDays(product, category);

        Assert.Null(resolved);
    }

    [Fact]
    public void Returns_Null_When_Product_Default_Is_Null_And_Category_Is_Null()
    {
        var product = NewProduct(defaultDueDays: null);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDays(product, category: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void ProductDefault_Wins_Even_When_Category_Is_Null()
    {
        var product = NewProduct(defaultDueDays: 7);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDays(product, category: null);

        Assert.Equal(7, resolved);
    }

    // ── ResolveDefaultDueDaysAfterOpening (plantry-1le6) ──────────────────────
    // Category carries no per-transition due-days field of its own (only the plain DefaultDueDays
    // covered above), so this resolves from the product alone — no category parameter to fall back to.

    [Fact]
    public void AfterOpening_Returns_ProductDefault_When_Set()
    {
        var product = Product.Create(HouseholdId, "Mustard", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, defaultDueDaysAfterOpening: 30, null, null, Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterOpening(product);

        Assert.Equal(30, resolved);
    }

    [Fact]
    public void AfterOpening_Returns_Null_When_Product_Has_No_Default()
    {
        var product = Product.Create(HouseholdId, "Rice", UnitId.New(), Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterOpening(product);

        Assert.Null(resolved);
    }

    // ── Freeze/thaw Never policy resolution ───────────────────────────────────

    [Fact]
    public void LocalTrue_Wins_And_Resolves_Never()
    {
        var product = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, null, defaultDueDaysAfterFreezing: 45, null, Clock);
        product.SetNeverExpiryOverrides(true, null, Clock);

        var resolved = ExpiryDefaultResolver.ResolveAfterFreezing(product, parent: null, householdDefault: 90);

        Assert.IsType<ExpiryTransitionPolicy.Never>(resolved);
    }

    [Fact]
    public void LocalFalse_Suppresses_ParentNever_And_Uses_ProductDays()
    {
        var parent = Product.Create(HouseholdId, "Chicken", UnitId.New(), Clock);
        parent.SetNeverExpiryOverrides(true, null, Clock);
        var variant = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        variant.MakeVariantOf(parent.Id, Clock);
        variant.SetExpiryDefaults(null, null, defaultDueDaysAfterFreezing: 12, null, Clock);
        variant.SetNeverExpiryOverrides(false, null, Clock);

        var resolved = ExpiryDefaultResolver.ResolveAfterFreezing(variant, parent, householdDefault: 90);

        Assert.Equal(new ExpiryTransitionPolicy.Days(12), resolved);
    }

    [Fact]
    public void NullVariantOverride_InheritsParentNever_Live()
    {
        var parent = Product.Create(HouseholdId, "Chicken", UnitId.New(), Clock);
        var variant = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        variant.MakeVariantOf(parent.Id, Clock);
        variant.SetNeverExpiryOverrides(null, null, Clock);

        parent.SetNeverExpiryOverrides(true, null, Clock);
        Assert.IsType<ExpiryTransitionPolicy.Never>(
            ExpiryDefaultResolver.ResolveAfterFreezing(variant, parent, householdDefault: 90));

        parent.SetNeverExpiryOverrides(false, null, Clock);
        Assert.Equal(new ExpiryTransitionPolicy.Days(90),
            ExpiryDefaultResolver.ResolveAfterFreezing(variant, parent, householdDefault: 90));
    }

    [Fact]
    public void VariantOverride_Detaches_From_Subsequent_ParentChanges()
    {
        var parent = Product.Create(HouseholdId, "Chicken", UnitId.New(), Clock);
        parent.SetNeverExpiryOverrides(true, null, Clock);
        var variant = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        variant.MakeVariantOf(parent.Id, Clock);
        variant.SetNeverExpiryOverrides(false, null, Clock);

        parent.SetNeverExpiryOverrides(false, null, Clock);
        Assert.Equal(new ExpiryTransitionPolicy.Days(90),
            ExpiryDefaultResolver.ResolveAfterFreezing(variant, parent, householdDefault: 90));

        parent.SetNeverExpiryOverrides(true, null, Clock);
        Assert.Equal(new ExpiryTransitionPolicy.Days(90),
            ExpiryDefaultResolver.ResolveAfterFreezing(variant, parent, householdDefault: 90));
    }

    [Fact]
    public void ProductDays_Beat_HouseholdFallback_When_NotNever()
    {
        var product = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, null, defaultDueDaysAfterFreezing: 45, null, Clock);

        Assert.Equal(new ExpiryTransitionPolicy.Days(45),
            ExpiryDefaultResolver.ResolveAfterFreezing(product, parent: null, householdDefault: 90));
    }

    [Fact]
    public void RootNullNever_Uses_HouseholdFallback()
    {
        var product = Product.Create(HouseholdId, "Leftover casserole", UnitId.New(), Clock);

        Assert.Equal(new ExpiryTransitionPolicy.Days(90),
            ExpiryDefaultResolver.ResolveAfterFreezing(product, parent: null, householdDefault: 90));
    }

    [Fact]
    public void Thawing_Uses_IndependentNeverFlag()
    {
        var product = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, null, null, defaultDueDaysAfterThawing: 5, Clock);
        product.SetNeverExpiryOverrides(null, true, Clock);

        Assert.IsType<ExpiryTransitionPolicy.Never>(
            ExpiryDefaultResolver.ResolveAfterThawing(product, parent: null, householdDefault: 3));
    }
}
