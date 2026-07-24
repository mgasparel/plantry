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
}
