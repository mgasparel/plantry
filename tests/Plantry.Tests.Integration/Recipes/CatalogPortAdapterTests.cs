using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.Recipes;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Recipes;

/// <summary>
/// L3 tests for the Recipes → Catalog anti-corruption adapters (P2-1b, recipes-domain-model.md §8),
/// wired over the REAL Catalog repositories/commands against a real Postgres schema. Proves the read
/// port surfaces product fields + the parent/variant tree, the writer mints an untracked staple
/// (track_stock = false, C12) and records a ProductConversion (C10), and the converter resolves a
/// quantity or fails loudly when no path exists (DM-12).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogPortAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private UnitId _gramsId;
    private UnitId _kilogramsId;
    private UnitId _cupsId;
    private UnitId _millilitresId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var kilograms = CatalogUnit.Create(_household, "kg", "kilograms", Dimension.Mass, 1000m);
        var cups = CatalogUnit.Create(_household, "cup", "cups", Dimension.Volume, 240m);
        var millilitres = CatalogUnit.Create(_household, "ml", "millilitres", Dimension.Volume, 1m, isBase: true);
        await catalog.Units.AddRangeAsync(grams, kilograms, cups, millilitres);
        await catalog.SaveChangesAsync();

        _gramsId = grams.Id;
        _kilogramsId = kilograms.Id;
        _cupsId = cups.Id;
        _millilitresId = millilitres.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Reader returns product name, track_stock and default unit")]
    public async Task Reader_Returns_Product_Fields()
    {
        ProductId productId;
        await using (var setup = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, Clock);
            await setup.Products.AddAsync(product);
            await setup.SaveChangesAsync();
            productId = product.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var result = await reader.FindAsync(productId.Value);

        Assert.NotNull(result);
        Assert.Equal("Flour", result!.Name);
        Assert.True(result.TrackStock);
        Assert.Equal(_gramsId.Value, result.DefaultUnitId);
        Assert.False(result.IsParent);
        Assert.Null(result.ParentProductId);
        Assert.Empty(result.VariantProductIds);
    }

    [Fact(DisplayName = "Reader exposes the parent/variant tree in both directions")]
    public async Task Reader_Exposes_Parent_Variant_Tree()
    {
        ProductId parentId;
        ProductId variantId;
        await using (var setup = NewCatalogDb())
        {
            var parent = Product.Create(_household, "Milk", _gramsId, Clock);
            parent.SetHasVariants(true, Clock);
            await setup.Products.AddAsync(parent);
            await setup.SaveChangesAsync();

            var skim = Product.Create(_household, "Milk (skim)", _gramsId, Clock);
            skim.MakeVariantOf(parent.Id, Clock);
            var whole = Product.Create(_household, "Milk (whole)", _gramsId, Clock);
            whole.MakeVariantOf(parent.Id, Clock);
            await setup.Products.AddRangeAsync(skim, whole);
            await setup.SaveChangesAsync();

            parentId = parent.Id;
            variantId = skim.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var parentView = await reader.FindAsync(parentId.Value);
        Assert.NotNull(parentView);
        Assert.True(parentView!.IsParent);
        Assert.Equal(2, parentView.VariantProductIds.Count);
        Assert.Contains(variantId.Value, parentView.VariantProductIds);

        var variantView = await reader.FindAsync(variantId.Value);
        Assert.NotNull(variantView);
        Assert.False(variantView!.IsParent);
        Assert.Equal(parentId.Value, variantView.ParentProductId);
    }

    [Fact(DisplayName = "Reader batch-resolves products WITH the variant tree in one round-trip, archived variants excluded")]
    public async Task Reader_FindManyWithVariants_Resolves_Tree_In_One_Query()
    {
        ProductId parentId;
        ProductId skimId;
        ProductId wholeId;
        ProductId flourId;
        await using (var setup = NewCatalogDb())
        {
            var parent = Product.Create(_household, "Milk", _gramsId, Clock);
            parent.SetHasVariants(true, Clock);
            await setup.Products.AddAsync(parent);
            await setup.SaveChangesAsync();

            var skim = Product.Create(_household, "Milk (skim)", _gramsId, Clock);
            skim.MakeVariantOf(parent.Id, Clock);
            var whole = Product.Create(_household, "Milk (whole)", _gramsId, Clock);
            whole.MakeVariantOf(parent.Id, Clock);
            var archivedVariant = Product.Create(_household, "Milk (lactose-free, discontinued)", _gramsId, Clock);
            archivedVariant.MakeVariantOf(parent.Id, Clock);
            archivedVariant.Archive(Clock); // must be excluded from the live rollup tree
            var flour = Product.Create(_household, "Flour", _gramsId, Clock); // a plain leaf
            await setup.Products.AddRangeAsync(skim, whole, archivedVariant, flour);
            await setup.SaveChangesAsync();

            parentId = parent.Id;
            skimId = skim.Id;
            wholeId = whole.Id;
            flourId = flour.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var resolved = await reader.FindManyWithVariantsAsync([parentId.Value, flourId.Value, Guid.NewGuid()]);

        Assert.Equal(2, resolved.Count); // the unknown id is omitted

        var parentView = resolved[parentId.Value];
        Assert.True(parentView.IsParent);
        Assert.Equal(2, parentView.VariantProductIds.Count); // archived variant excluded
        Assert.Contains(skimId.Value, parentView.VariantProductIds);
        Assert.Contains(wholeId.Value, parentView.VariantProductIds);

        var flourView = resolved[flourId.Value];
        Assert.False(flourView.IsParent);
        Assert.Empty(flourView.VariantProductIds);

        Assert.Empty(await reader.FindManyWithVariantsAsync([]));
    }

    [Fact(DisplayName = "Reader search returns name-matching candidates and ignores a blank query")]
    public async Task Reader_Search_Returns_Candidates()
    {
        await using (var setup = NewCatalogDb())
        {
            await setup.Products.AddRangeAsync(
                Product.Create(_household, "Flour", _gramsId, Clock),
                Product.Create(_household, "Flax seed", _gramsId, Clock),
                Product.Create(_household, "Sugar", _gramsId, Clock));
            await setup.SaveChangesAsync();
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var flMatches = await reader.SearchAsync("fl");
        Assert.Equal(2, flMatches.Count);
        Assert.Contains(flMatches, c => c.Name == "Flour");
        Assert.Contains(flMatches, c => c.Name == "Flax seed");

        // Fuzzy ranker (plantry-hl4a): "FLOUR" may surface near-misses like "Flax seed" above the
        // 0.70 cutoff.  Assert the exact match is present and ranked first; don't constrain count.
        var flourMatches = await reader.SearchAsync("FLOUR");
        Assert.NotEmpty(flourMatches);
        Assert.Equal("Flour", flourMatches[0].Name);
        Assert.Equal(1.0, flourMatches[0].Score, precision: 2);

        Assert.Empty(await reader.SearchAsync("   "));
    }

    [Fact(DisplayName = "Reader batch-resolves product summaries and unit codes for an ingredient list")]
    public async Task Reader_Batch_Resolves_Summaries_And_Unit_Codes()
    {
        ProductId trackedId;
        ProductId stapleId;
        await using (var setup = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            var salt = Product.Create(_household, "Salt", _gramsId, Clock);
            salt.SetTrackStock(false, Clock); // untracked staple (C12)
            await setup.Products.AddRangeAsync(flour, salt);
            await setup.SaveChangesAsync();
            trackedId = flour.Id;
            stapleId = salt.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var summaries = await reader.ResolveSummariesAsync([trackedId.Value, stapleId.Value, Guid.NewGuid()]);
        Assert.Equal(2, summaries.Count); // the unknown id is omitted
        Assert.Equal("Flour", summaries[trackedId.Value].Name);
        Assert.True(summaries[trackedId.Value].TrackStock);
        Assert.False(summaries[stapleId.Value].TrackStock);

        var codes = await reader.ResolveUnitCodesAsync([_gramsId.Value, _cupsId.Value, Guid.NewGuid()]);
        Assert.Equal(2, codes.Count); // the unknown id is omitted
        Assert.Equal("g", codes[_gramsId.Value]);
        Assert.Equal("cup", codes[_cupsId.Value]);

        Assert.Empty(await reader.ResolveSummariesAsync([]));
        Assert.Empty(await reader.ResolveUnitCodesAsync([]));
    }

    [Fact(DisplayName = "FindManyAsync batch-resolves lookups (name/track_stock/default unit), dedups input, omits unknown ids")]
    public async Task Reader_FindMany_Resolves_Lookups()
    {
        ProductId trackedId;
        ProductId stapleId;
        await using (var setup = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            var salt = Product.Create(_household, "Salt", _gramsId, Clock);
            salt.SetTrackStock(false, Clock); // untracked staple (C12)
            await setup.Products.AddRangeAsync(flour, salt);
            await setup.SaveChangesAsync();
            trackedId = flour.Id;
            stapleId = salt.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        // Duplicate input id + one unknown id; the lookup carries DefaultUnitId — the field
        // ResolveSummariesAsync omits and the reason FindManyAsync exists.
        var lookups = await reader.FindManyAsync([trackedId.Value, stapleId.Value, trackedId.Value, Guid.NewGuid()]);

        Assert.Equal(2, lookups.Count); // duplicate deduped, unknown id omitted

        var flourLookup = lookups[trackedId.Value];
        Assert.Equal("Flour", flourLookup.Name);
        Assert.True(flourLookup.TrackStock);
        Assert.Equal(_gramsId.Value, flourLookup.DefaultUnitId);

        Assert.False(lookups[stapleId.Value].TrackStock);

        Assert.Empty(await reader.FindManyAsync([]));
    }

    [Fact(DisplayName = "FindManyAsync resolves an archived product (ListWithConversionsAsync has no archived filter)")]
    public async Task Reader_FindMany_Resolves_Archived_Product()
    {
        // Holds only by omission today — pins the semantic against a future "filter archived" change.
        ProductId archivedId;
        await using (var setup = NewCatalogDb())
        {
            var product = Product.Create(_household, "Discontinued Sprinkles", _gramsId, Clock);
            product.Archive(Clock);
            await setup.Products.AddAsync(product);
            await setup.SaveChangesAsync();
            archivedId = product.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new CatalogProductReaderAdapter(new ProductRepository(read), new UnitRepository(read), new CategoryRepository(read));

        var lookups = await reader.FindManyAsync([archivedId.Value]);

        Assert.True(lookups.ContainsKey(archivedId.Value));
        Assert.Equal("Discontinued Sprinkles", lookups[archivedId.Value].Name);
    }

    [Fact(DisplayName = "Writer inline-creates an untracked staple (track_stock = false)")]
    public async Task Writer_Creates_Untracked_Staple()
    {
        await using var act = NewCatalogDb();
        var writer = NewWriter(act);

        var stapleId = await writer.CreateUntrackedStapleAsync("Salt", _gramsId.Value);

        await using var verify = NewCatalogDb();
        var product = await verify.Products.SingleAsync(p => p.Id == ProductId.From(stapleId));
        Assert.Equal("Salt", product.Name);
        Assert.False(product.TrackStock);
        Assert.Equal(_gramsId, product.DefaultUnitId);
    }

    [Fact(DisplayName = "Writer adds a ProductConversion to a product")]
    public async Task Writer_Adds_ProductConversion()
    {
        ProductId productId;
        await using (var setup = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, Clock);
            await setup.Products.AddAsync(product);
            await setup.SaveChangesAsync();
            productId = product.Id;
        }

        await using (var act = NewCatalogDb())
        {
            await NewWriter(act).AddConversionAsync(productId.Value, _cupsId.Value, _gramsId.Value, 120m);
        }

        await using var verify = NewCatalogDb();
        var loaded = await verify.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);
        var conversion = Assert.Single(loaded.Conversions);
        Assert.Equal(_cupsId, conversion.FromUnitId);
        Assert.Equal(_gramsId, conversion.ToUnitId);
        Assert.Equal(120m, conversion.Factor);
    }

    [Fact(DisplayName = "Converter resolves via same-dimension scaling and a product conversion")]
    public async Task Converter_Resolves_Quantity()
    {
        ProductId productId;
        await using (var setup = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, Clock);
            product.AddConversion(_cupsId, _gramsId, 120m, Clock); // 1 cup flour = 120 g
            await setup.Products.AddAsync(product);
            await setup.SaveChangesAsync();
            productId = product.Id;
        }

        await using var read = NewCatalogDb();
        var converter = NewConverter(read);

        // Same dimension (mass): 2 kg → 2000 g.
        var massResult = await converter.ConvertAsync(productId.Value, 2m, _kilogramsId.Value, _gramsId.Value);
        Assert.True(massResult.IsSuccess);
        Assert.Equal(2000m, massResult.Value);

        // Cross-dimension via the product conversion: 2 cups → 240 g.
        var densityResult = await converter.ConvertAsync(productId.Value, 2m, _cupsId.Value, _gramsId.Value);
        Assert.True(densityResult.IsSuccess);
        Assert.Equal(240m, densityResult.Value);
    }

    [Fact(DisplayName = "Converter fails loudly when no conversion path exists")]
    public async Task Converter_Fails_Loudly_When_No_Path()
    {
        ProductId productId;
        await using (var setup = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, Clock); // no cross-dimension conversion
            await setup.Products.AddAsync(product);
            await setup.SaveChangesAsync();
            productId = product.Id;
        }

        await using var read = NewCatalogDb();
        var converter = NewConverter(read);

        var result = await converter.ConvertAsync(productId.Value, 100m, _gramsId.Value, _millilitresId.Value);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.UnresolvableConversion", result.Error.Code);
    }

    [Fact(DisplayName = "FindUnconvertiblePathsAsync batch-resolves many triples in one pass, dedups duplicates, returns only the unconvertible ones (plantry-4t0g)")]
    public async Task Converter_BatchFindsUnconvertiblePaths()
    {
        ProductId flourId;
        ProductId sugarId;
        await using (var setup = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            flour.AddConversion(_cupsId, _gramsId, 120m, Clock); // 1 cup flour = 120 g
            var sugar = Product.Create(_household, "Sugar", _gramsId, Clock); // no cross-dimension conversion
            await setup.Products.AddRangeAsync(flour, sugar);
            await setup.SaveChangesAsync();
            flourId = flour.Id;
            sugarId = sugar.Id;
        }

        await using var read = NewCatalogDb();
        var converter = NewConverter(read);

        var triples = new (Guid, Guid, Guid)[]
        {
            (flourId.Value, _cupsId.Value, _gramsId.Value),      // has a path (product conversion)
            (flourId.Value, _kilogramsId.Value, _gramsId.Value), // has a path (same-dimension scaling)
            (sugarId.Value, _cupsId.Value, _gramsId.Value),      // no path
            (sugarId.Value, _cupsId.Value, _gramsId.Value),      // duplicate — must be deduped, not double-counted
        };

        var unconvertible = await converter.FindUnconvertiblePathsAsync(triples);

        Assert.Single(unconvertible);
        Assert.Contains((sugarId.Value, _cupsId.Value, _gramsId.Value), unconvertible);

        Assert.Empty(await converter.FindUnconvertiblePathsAsync([]));
    }

    private CatalogWriterAdapter NewWriter(CatalogDbContext ctx) =>
        new(
            new ProductRepository(ctx),
            new UnitRepository(ctx),
            new CategoryRepository(ctx),
            new LocationRepository(ctx),
            Clock,
            new TestTenant(_household.Value));

    private RecipesUnitConverterAdapter NewConverter(CatalogDbContext ctx) =>
        new(new ProductRepository(ctx), new UnitRepository(ctx));

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }

    private sealed class TestTenant(Guid household) : ITenantContext
    {
        public Guid? HouseholdId { get; } = household;
    }
}
