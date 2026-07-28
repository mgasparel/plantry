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

    // ── ResolveDefaultDueDaysAfterFreezing / AfterThawing (plantry-hh1f) ──────
    // Unlike AfterOpening, freeze/thaw now have a backstop: the household-wide default. The product's
    // own override still wins when set; the household default only applies when it's unset — exactly
    // the auto-created-leftovers gap plantry-hh1f reported (no category, no per-product override).

    [Fact]
    public void AfterFreezing_ProductOverride_Wins_Over_HouseholdDefault()
    {
        var product = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, null, defaultDueDaysAfterFreezing: 45, null, Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterFreezing(product, householdDefault: 90);

        Assert.Equal(45, resolved);
    }

    [Fact]
    public void AfterFreezing_Falls_Back_To_HouseholdDefault_When_Product_Has_No_Override()
    {
        // No category, no per-product override — the exact shape of an auto-created leftovers product
        // (CookRecipe.cs:214, categoryId: null) that originally reported this ticket.
        var product = Product.Create(HouseholdId, "Leftover casserole", UnitId.New(), Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterFreezing(product, householdDefault: 90);

        Assert.Equal(90, resolved);
    }

    [Fact]
    public void AfterThawing_ProductOverride_Wins_Over_HouseholdDefault()
    {
        var product = Product.Create(HouseholdId, "Chicken thighs", UnitId.New(), Clock);
        product.SetExpiryDefaults(null, null, null, defaultDueDaysAfterThawing: 5, Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterThawing(product, householdDefault: 3);

        Assert.Equal(5, resolved);
    }

    [Fact]
    public void AfterThawing_Falls_Back_To_HouseholdDefault_When_Product_Has_No_Override()
    {
        var product = Product.Create(HouseholdId, "Leftover casserole", UnitId.New(), Clock);

        var resolved = ExpiryDefaultResolver.ResolveDefaultDueDaysAfterThawing(product, householdDefault: 3);

        Assert.Equal(3, resolved);
    }
}
