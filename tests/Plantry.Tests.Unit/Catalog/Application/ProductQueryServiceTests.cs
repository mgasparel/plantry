using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Catalog.Application;

/// <summary>
/// L1 unit tests for <see cref="ProductQueryService.DefaultExpiryDateAsync"/> (plantry-g9jq) — the
/// DM-11 default-expiry composition extracted from the duplicated <c>ResolveExpiryAsync</c> helpers
/// on Pantry Index and Product Detail. Covers the full <see cref="ExpiryDefaultResolver"/> fallback
/// chain (product wins over category, category fallback, neither → null) plus the unknown-product
/// guard and the today+dueDays materialization.
/// </summary>
public sealed class ProductQueryServiceTests
{
    private static readonly HouseholdId HouseholdId = Plantry.SharedKernel.HouseholdId.New();
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static ProductQueryService MakeService(
        out FakeProductRepository products, out FakeCategoryRepository categories)
    {
        products = new FakeProductRepository();
        categories = new FakeCategoryRepository();
        var units = new FakeUnitRepository();
        var locations = new FakeLocationRepository();
        return new ProductQueryService(products, units, categories, locations, new FakeClock());
    }

    [Fact]
    public async Task Product_Default_Wins_Over_Category_Default()
    {
        var service = MakeService(out var products, out var categories);
        var category = Category.Create(HouseholdId, "Dairy", defaultDueDays: 30);
        categories.Items.Add(category);

        var product = Product.Create(HouseholdId, "Whole Milk", UnitId.New(), SystemClock.Instance);
        product.SetCategory(category.Id, SystemClock.Instance);
        product.SetExpiryDefaults(defaultDueDays: 7, null, null, null, SystemClock.Instance);
        products.Items.Add(product);

        var result = await service.DefaultExpiryDateAsync(product.Id.Value);

        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(7), result);
    }

    [Fact]
    public async Task Falls_Back_To_Category_Default_When_Product_Has_None()
    {
        var service = MakeService(out var products, out var categories);
        var category = Category.Create(HouseholdId, "Dairy", defaultDueDays: 30);
        categories.Items.Add(category);

        var product = Product.Create(HouseholdId, "Whole Milk", UnitId.New(), SystemClock.Instance);
        product.SetCategory(category.Id, SystemClock.Instance);
        products.Items.Add(product);

        var result = await service.DefaultExpiryDateAsync(product.Id.Value);

        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(30), result);
    }

    [Fact]
    public async Task Returns_Null_When_Neither_Product_Nor_Category_Has_A_Default()
    {
        var service = MakeService(out var products, out var categories);
        var category = Category.Create(HouseholdId, "Dairy");
        categories.Items.Add(category);

        var product = Product.Create(HouseholdId, "Whole Milk", UnitId.New(), SystemClock.Instance);
        product.SetCategory(category.Id, SystemClock.Instance);
        products.Items.Add(product);

        var result = await service.DefaultExpiryDateAsync(product.Id.Value);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_Null_When_Product_Has_No_Category()
    {
        var service = MakeService(out var products, out _);
        var product = Product.Create(HouseholdId, "Whole Milk", UnitId.New(), SystemClock.Instance);
        products.Items.Add(product);

        var result = await service.DefaultExpiryDateAsync(product.Id.Value);

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_Null_For_Unknown_Product()
    {
        var service = MakeService(out _, out _);

        var result = await service.DefaultExpiryDateAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}
