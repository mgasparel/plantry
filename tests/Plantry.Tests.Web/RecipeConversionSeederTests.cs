using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Recipes;

namespace Plantry.Tests.Web;

/// <summary>
/// Unit-level tests for <see cref="RecipeConversionSeeder"/> (plantry-qll2.4) — the async seeding worker
/// behind the recipe-save unit-conversion trigger. Exercised against in-memory fakes (no host, no live
/// LLM) to pin the acceptance-critical guards:
/// <list type="bullet">
///   <item>a gap with no existing bridge is inferred and recorded as an <c>ai_suggested</c> conversion;</item>
///   <item>a pair already bridged (any provenance) is skipped with <b>no</b> inference call (criteria 2 &amp; 3,
///   robust to the save→seed race);</item>
///   <item>a soft-failed / null inference seeds nothing (leaving today's unit-gap behaviour);</item>
///   <item>duplicate requests collapse to a single inference + conversion.</item>
/// </list>
/// </summary>
public sealed class RecipeConversionSeederTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.From(Guid.NewGuid());

    private static Product TrackedProduct(UnitId defaultUnit, string name = "Cashews") =>
        Product.Create(Household, name, defaultUnit, Clock);

    [Fact]
    public async Task Seeds_An_Ai_Suggested_Conversion_For_An_Unbridged_Gap()
    {
        var gram = UnitId.New();
        var cup = UnitId.New();
        var product = TrackedProduct(gram);
        var repo = new FakeProductRepository(product);
        var inferrer = new FakeInferrer(available: true, factor: 120m);
        var seeder = new RecipeConversionSeeder(repo, inferrer, Clock, NullLogger<RecipeConversionSeeder>.Instance);

        var seeded = await seeder.SeedAsync([Gap(product.Id.Value, cup.Value, "cup", gram.Value, "g")]);

        Assert.Equal(1, seeded);
        Assert.Equal(1, inferrer.CallCount);
        Assert.Equal(1, repo.SaveCount);
        var conversion = Assert.Single(product.Conversions);
        Assert.Equal(cup, conversion.FromUnitId);
        Assert.Equal(gram, conversion.ToUnitId);
        Assert.Equal(120m, conversion.Factor);
        Assert.True(conversion.IsAiSuggested);
    }

    [Fact]
    public async Task Skips_Inference_When_The_Pair_Is_Already_Bridged()
    {
        var gram = UnitId.New();
        var cup = UnitId.New();
        var product = TrackedProduct(gram);
        // A conversion for the exact ordered pair already exists (e.g. a sibling recipe seeded it, or the
        // user added one between save and this run) — the seeder must NOT call the LLM again (criteria 2 & 3).
        product.AddConversion(cup, gram, 118m, Clock, ConversionSource.UserConfirmed);

        var repo = new FakeProductRepository(product);
        var inferrer = new FakeInferrer(available: true, factor: 120m);
        var seeder = new RecipeConversionSeeder(repo, inferrer, Clock, NullLogger<RecipeConversionSeeder>.Instance);

        var seeded = await seeder.SeedAsync([Gap(product.Id.Value, cup.Value, "cup", gram.Value, "g")]);

        Assert.Equal(0, seeded);
        Assert.Equal(0, inferrer.CallCount);        // no LLM call
        Assert.Equal(0, repo.SaveCount);            // nothing written
        var conversion = Assert.Single(product.Conversions);
        Assert.Equal(118m, conversion.Factor);      // the existing factor is untouched
        Assert.False(conversion.IsAiSuggested);
    }

    [Fact]
    public async Task Seeds_Nothing_When_Inference_Soft_Fails()
    {
        var gram = UnitId.New();
        var cup = UnitId.New();
        var product = TrackedProduct(gram);
        var repo = new FakeProductRepository(product);
        var inferrer = new FakeInferrer(available: true, factor: null); // soft failure
        var seeder = new RecipeConversionSeeder(repo, inferrer, Clock, NullLogger<RecipeConversionSeeder>.Instance);

        var seeded = await seeder.SeedAsync([Gap(product.Id.Value, cup.Value, "cup", gram.Value, "g")]);

        Assert.Equal(0, seeded);
        Assert.Equal(1, inferrer.CallCount);
        Assert.Equal(0, repo.SaveCount);
        Assert.Empty(product.Conversions);
    }

    [Fact]
    public async Task Collapses_Duplicate_Requests_To_A_Single_Inference()
    {
        var gram = UnitId.New();
        var cup = UnitId.New();
        var product = TrackedProduct(gram);
        var repo = new FakeProductRepository(product);
        var inferrer = new FakeInferrer(available: true, factor: 120m);
        var seeder = new RecipeConversionSeeder(repo, inferrer, Clock, NullLogger<RecipeConversionSeeder>.Instance);

        var gap = Gap(product.Id.Value, cup.Value, "cup", gram.Value, "g");
        var seeded = await seeder.SeedAsync([gap, gap]);

        Assert.Equal(1, seeded);
        Assert.Equal(1, inferrer.CallCount);
        Assert.Single(product.Conversions);
    }

    [Fact]
    public async Task Commits_Each_Seeded_Product_In_Its_Own_Transaction()
    {
        var gram = UnitId.New();
        var cup = UnitId.New();
        var cashews = TrackedProduct(gram, "Cashews");
        var flour = TrackedProduct(gram, "Flour");
        var repo = new FakeProductRepository(cashews, flour);
        var inferrer = new FakeInferrer(available: true, factor: 120m);
        var seeder = new RecipeConversionSeeder(repo, inferrer, Clock, NullLogger<RecipeConversionSeeder>.Instance);

        var seeded = await seeder.SeedAsync(
        [
            Gap(cashews.Id.Value, cup.Value, "cup", gram.Value, "g"),
            Gap(flour.Id.Value, cup.Value, "cup", gram.Value, "g"),
        ]);

        Assert.Equal(2, seeded);
        // One SaveChanges per mutated aggregate (Gate 2), never a single cross-aggregate save.
        Assert.Equal(2, repo.SaveCount);
        Assert.Single(cashews.Conversions);
        Assert.Single(flour.Conversions);
    }

    private static ConversionSeedRequest Gap(
        Guid productId, Guid fromUnit, string fromCode, Guid toUnit, string toCode) =>
        new(productId, "Cashews", fromUnit, fromCode, toUnit, toCode);

    // ── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class FakeInferrer(bool available, decimal? factor) : IIngredientConversionInferrer
    {
        public int CallCount { get; private set; }
        public bool IsAvailable => available;

        public Task<decimal?> InferFactorAsync(
            string productName, string fromUnitCode, string toUnitCode, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(factor);
        }
    }

    private sealed class FakeProductRepository(params Product[] products) : IProductRepository
    {
        private readonly List<Product> _products = [.. products];
        public int SaveCount { get; private set; }

        public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default)
        {
            var wanted = ids.ToHashSet();
            return Task.FromResult(_products.Where(p => wanted.Contains(p.Id)).ToList());
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
            Task.FromResult(_products.FirstOrDefault(p => p.Id == id));
        public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Product product, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
