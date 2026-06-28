using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// Unit-style tests for <see cref="WeekBag"/> in-memory lookup helpers (no DB required).
/// These live in the Integration test project because <see cref="WeekBag"/> is defined in
/// Plantry.Web, which the unit test project does not reference.
/// </summary>
public sealed class WeekBagTests
{
    private static readonly Guid RecipeId1 = Guid.NewGuid();
    private static readonly Guid ProductId1 = Guid.NewGuid();
    private static readonly Guid UnitId1 = Guid.NewGuid();
    private static readonly Guid UnitId2 = Guid.NewGuid();

    private static WeekBag BuildBag() =>
        new(
            recipes: new Dictionary<Guid, RecipeFact>
            {
                [RecipeId1] = new RecipeFact(RecipeId1, "Pasta", 4),
            },
            ingredientsByRecipe: new Dictionary<Guid, IReadOnlyList<IngredientFact>>
            {
                [RecipeId1] = [new IngredientFact(Guid.NewGuid(), RecipeId1, ProductId1, 200m, UnitId1, 1)],
            },
            products: new Dictionary<Guid, ProductFact>
            {
                [ProductId1] = new ProductFact(ProductId1, "Flour", TrackStock: true, DefaultUnitId: UnitId1,
                    ParentProductId: null, HasVariants: false, Archived: false, VariantProductIds: []),
            },
            conversionsByProduct: new Dictionary<Guid, IReadOnlyList<ConversionFact>>
            {
                [ProductId1] = [new ConversionFact(ProductId1, UnitId1, UnitId2, 0.001m)],
            },
            units: new Dictionary<Guid, UnitFact>
            {
                [UnitId1] = new UnitFact(UnitId1, "g", "grams", "mass", 1m, IsBase: true),
                [UnitId2] = new UnitFact(UnitId2, "kg", "kilograms", "mass", 1000m, IsBase: false),
            },
            stockByProduct: new Dictionary<Guid, StockFact>
            {
                [ProductId1] = new StockFact(ProductId1,
                    Lots: [new StockLotFact(ProductId1, UnitId1, 500m)],
                    SoonestExpiry: new DateOnly(2026, 8, 1)),
            },
            latestPriceByProduct: new Dictionary<Guid, PriceFact>
            {
                [ProductId1] = new PriceFact(ProductId1, Price: 2.50m, Quantity: 500m,
                    UnitId: UnitId1, UnitPrice: 0.005m, ObservedAt: DateTime.UtcNow),
            });

    [Fact(DisplayName = "GetRecipe returns the correct fact when present")]
    public void GetRecipe_ReturnsCorrectFact_WhenPresent()
    {
        var bag = BuildBag();
        var recipe = bag.GetRecipe(RecipeId1);
        Assert.NotNull(recipe);
        Assert.Equal("Pasta", recipe.Name);
        Assert.Equal(4, recipe.DefaultServings);
    }

    [Fact(DisplayName = "GetRecipe returns null when recipe not in bag")]
    public void GetRecipe_ReturnsNull_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Null(bag.GetRecipe(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetIngredients returns the ingredient list for a recipe")]
    public void GetIngredients_ReturnsList_WhenPresent()
    {
        var bag = BuildBag();
        var ingredients = bag.GetIngredients(RecipeId1);
        Assert.Single(ingredients);
        Assert.Equal(ProductId1, ingredients[0].ProductId);
        Assert.Equal(200m, ingredients[0].Quantity);
    }

    [Fact(DisplayName = "GetIngredients returns empty list when recipe not in bag")]
    public void GetIngredients_ReturnsEmpty_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Empty(bag.GetIngredients(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetProduct returns the correct product fact when present")]
    public void GetProduct_ReturnsCorrectFact_WhenPresent()
    {
        var bag = BuildBag();
        var product = bag.GetProduct(ProductId1);
        Assert.NotNull(product);
        Assert.Equal("Flour", product.Name);
        Assert.True(product.TrackStock);
        Assert.Equal(UnitId1, product.DefaultUnitId);
    }

    [Fact(DisplayName = "GetProduct returns null when product not in bag")]
    public void GetProduct_ReturnsNull_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Null(bag.GetProduct(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetConversions returns conversion list for a product")]
    public void GetConversions_ReturnsList_WhenPresent()
    {
        var bag = BuildBag();
        var conversions = bag.GetConversions(ProductId1);
        Assert.Single(conversions);
        Assert.Equal(UnitId1, conversions[0].FromUnitId);
        Assert.Equal(UnitId2, conversions[0].ToUnitId);
        Assert.Equal(0.001m, conversions[0].Factor);
    }

    [Fact(DisplayName = "GetConversions returns empty list when product not in bag")]
    public void GetConversions_ReturnsEmpty_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Empty(bag.GetConversions(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetUnit returns the correct unit fact when present")]
    public void GetUnit_ReturnsCorrectFact_WhenPresent()
    {
        var bag = BuildBag();
        var unit = bag.GetUnit(UnitId1);
        Assert.NotNull(unit);
        Assert.Equal("g", unit.Code);
        Assert.True(unit.IsBase);
    }

    [Fact(DisplayName = "GetUnit returns null when unit not in bag")]
    public void GetUnit_ReturnsNull_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Null(bag.GetUnit(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetStock returns the correct stock snapshot when present")]
    public void GetStock_ReturnsCorrectSnapshot_WhenPresent()
    {
        var bag = BuildBag();
        var stock = bag.GetStock(ProductId1);
        Assert.NotNull(stock);
        Assert.True(stock.HasStock);
        Assert.Equal(new DateOnly(2026, 8, 1), stock.SoonestExpiry);
        Assert.Single(stock.Lots);
        Assert.Equal(500m, stock.Lots[0].TotalQuantity);
    }

    [Fact(DisplayName = "GetStock returns null when product has no stock")]
    public void GetStock_ReturnsNull_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Null(bag.GetStock(Guid.NewGuid()));
    }

    [Fact(DisplayName = "GetLatestPrice returns the correct price fact when present")]
    public void GetLatestPrice_ReturnsCorrectFact_WhenPresent()
    {
        var bag = BuildBag();
        var price = bag.GetLatestPrice(ProductId1);
        Assert.NotNull(price);
        Assert.Equal(2.50m, price.Price);
        Assert.Equal(0.005m, price.UnitPrice);
    }

    [Fact(DisplayName = "GetLatestPrice returns null when product has no price data")]
    public void GetLatestPrice_ReturnsNull_WhenNotPresent()
    {
        var bag = BuildBag();
        Assert.Null(bag.GetLatestPrice(Guid.NewGuid()));
    }

    [Fact(DisplayName = "StockFact.HasStock is true when Lots is non-empty")]
    public void StockFact_HasStock_IsFalse_WhenLotsEmpty()
    {
        var emptyStock = new StockFact(Guid.NewGuid(), Lots: [], SoonestExpiry: null);
        Assert.False(emptyStock.HasStock);
    }
}
