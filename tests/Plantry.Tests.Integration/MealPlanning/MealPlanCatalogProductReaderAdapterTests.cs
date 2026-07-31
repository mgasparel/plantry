using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>
/// L3 tests for <see cref="MealPlanCatalogProductReaderAdapter"/> against a real Postgres schema
/// (plantry-pt79). Proves that parent (grouping) products are excluded from the meal-editor product
/// search and rejected by the plannability check, while their concrete variants and unrelated leaf
/// products are unaffected — a parent has no resolution point for "which variant was consumed", so
/// it cannot be planned as a direct product dish.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MealPlanCatalogProductReaderAdapterTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly IClock Clock = SystemClock.Instance;
    private HouseholdId _household;
    private UnitId _gramsId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var catalog = NewCatalogDb();
        var grams = Plantry.Catalog.Domain.Unit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        await catalog.Units.AddAsync(grams);
        await catalog.SaveChangesAsync();
        _gramsId = grams.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "SearchAsync excludes the parent product but still returns its variants and unrelated leaf products")]
    public async Task SearchAsync_Excludes_Parent_Includes_Variants_And_Leaves()
    {
        ProductId parentId, skimId, wholeId, flourId;
        await using (var setup = NewCatalogDb())
        {
            var parent = Product.Create(_household, "Black Beans", _gramsId, Clock);
            parent.SetHasVariants(true, Clock);
            await setup.Products.AddAsync(parent);
            await setup.SaveChangesAsync();

            var skim = Product.Create(_household, "Black Beans (canned)", _gramsId, Clock);
            skim.MakeVariantOf(parent.Id, Clock);
            var whole = Product.Create(_household, "Black Beans (dried)", _gramsId, Clock);
            whole.MakeVariantOf(parent.Id, Clock);
            var flour = Product.Create(_household, "Flour", _gramsId, Clock); // unrelated leaf
            await setup.Products.AddRangeAsync(skim, whole, flour);
            await setup.SaveChangesAsync();

            parentId = parent.Id;
            skimId = skim.Id;
            wholeId = whole.Id;
            flourId = flour.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        var beansResults = await reader.SearchAsync("Black Beans");
        Assert.Equal(2, beansResults.Count);
        Assert.DoesNotContain(beansResults, p => p.ProductId == parentId.Value);
        Assert.Contains(beansResults, p => p.ProductId == skimId.Value);
        Assert.Contains(beansResults, p => p.ProductId == wholeId.Value);

        var flourResults = await reader.SearchAsync("Flour");
        Assert.Single(flourResults);
        Assert.Equal(flourId.Value, flourResults[0].ProductId);
    }

    [Fact(DisplayName = "IsPlannableAsync rejects a parent product, accepts a concrete product, and rejects an unknown/archived id")]
    public async Task IsPlannableAsync_Rejects_Parent_Accepts_Concrete()
    {
        ProductId parentId, flourId, archivedId;
        await using (var setup = NewCatalogDb())
        {
            var parent = Product.Create(_household, "Milk", _gramsId, Clock);
            parent.SetHasVariants(true, Clock);
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            var archived = Product.Create(_household, "Discontinued", _gramsId, Clock);
            archived.Archive(Clock);
            await setup.Products.AddRangeAsync(parent, flour, archived);
            await setup.SaveChangesAsync();

            parentId = parent.Id;
            flourId = flour.Id;
            archivedId = archived.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        Assert.False(await reader.IsPlannableAsync(parentId.Value));
        Assert.True(await reader.IsPlannableAsync(flourId.Value));
        Assert.False(await reader.IsPlannableAsync(archivedId.Value));
        Assert.False(await reader.IsPlannableAsync(Guid.NewGuid()));

        // ExistsAsync still reports the parent as existing — only plannability is narrowed.
        Assert.True(await reader.ExistsAsync(parentId.Value));

        // Grandfathering (plantry-pt79 §3): a planned dish that already references a parent must
        // still resolve its name so the week grid can render + delete it — ResolveNamesAsync is
        // intentionally untouched by the parent-exclusion filter above.
        var names = await reader.ResolveNamesAsync([parentId.Value]);
        Assert.Equal("Milk", names[parentId.Value]);
    }

    [Fact(DisplayName = "SearchAsync and ResolveDefaultUnitCodesAsync resolve each product's default unit CODE (plantry-ri26)")]
    public async Task UnitCode_Resolution_Matches_Each_Products_Default_Unit()
    {
        UnitId poundsId;
        ProductId flourId, sugarId;
        await using (var setup = NewCatalogDb())
        {
            var pounds = Plantry.Catalog.Domain.Unit.Create(_household, "lb", "pounds", Dimension.Mass, 453.592m, isBase: false);
            await setup.Units.AddAsync(pounds);
            await setup.SaveChangesAsync();
            poundsId = pounds.Id;

            // Flour defaults to the fixture's grams unit ("g"); Sugar defaults to pounds ("lb") — two
            // distinct products with two distinct default units, so a lookup that silently returned
            // the same code for both (e.g. always the first unit found) would be caught.
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            var sugar = Product.Create(_household, "Sugar", poundsId, Clock);
            await setup.Products.AddRangeAsync(flour, sugar);
            await setup.SaveChangesAsync();

            flourId = flour.Id;
            sugarId = sugar.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        // SearchAsync (the dish-search hop) carries each hit's own unit code.
        var searchResults = await reader.SearchAsync("");
        Assert.Contains(searchResults, p => p.ProductId == flourId.Value && p.UnitCode == "g");
        Assert.Contains(searchResults, p => p.ProductId == sugarId.Value && p.UnitCode == "lb");

        // ResolveDefaultUnitCodesAsync (the editor-hydration / week-load hop) batch-resolves the
        // same codes by id, and simply omits an unknown id rather than throwing.
        var unknownId = Guid.NewGuid();
        var resolved = await reader.ResolveDefaultUnitCodesAsync([flourId.Value, sugarId.Value, unknownId]);
        Assert.Equal("g", resolved[flourId.Value]);
        Assert.Equal("lb", resolved[sugarId.Value]);
        Assert.False(resolved.ContainsKey(unknownId));
    }

    [Fact(DisplayName = "ResolveDefaultUnitCodesAsync issues exactly one units query, memoised for the adapter's lifetime, per-instance not shared (plantry-jefp AC1-3)")]
    public async Task GetUnitCodesByIdAsync_Is_Memoised_Per_Adapter_Instance()
    {
        ProductId flourId;
        await using (var setup = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            await setup.Products.AddAsync(flour);
            await setup.SaveChangesAsync();
            flourId = flour.Id;
        }

        var counter = new QueryCountingInterceptor();
        await using var read = NewCatalogDb(counter);
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        // AC1 — baseline: one call yields exactly one units query. This is the guard against a
        // wrong CountMatching fragment silently making AC2 pass vacuously (a 0-vs-0 comparison
        // would never fail).
        await reader.ResolveDefaultUnitCodesAsync([flourId.Value]);
        Assert.Equal(1, counter.CountMatching("units"));

        // AC2 — memoised: a second call on the SAME adapter instance leaves the count at 1.
        await reader.ResolveDefaultUnitCodesAsync([flourId.Value]);
        Assert.Equal(1, counter.CountMatching("units"));

        // AC3 — per-scope, not static: a NEW adapter instance over a NEW context takes the count
        // to 2. Regression guard against the cache becoming static/shared.
        await using var read2 = NewCatalogDb(counter);
        var reader2 = new MealPlanCatalogProductReaderAdapter(read2);
        await reader2.ResolveDefaultUnitCodesAsync([flourId.Value]);
        Assert.Equal(2, counter.CountMatching("units"));
    }

    [Fact(DisplayName = "SearchAsync short-circuits the units query when the name filter matches zero products (plantry-jefp AC4)")]
    public async Task SearchAsync_ZeroMatches_Skips_UnitCodes_Query()
    {
        await using (var setup = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, Clock);
            await setup.Products.AddAsync(flour);
            await setup.SaveChangesAsync();
        }

        var counter = new QueryCountingInterceptor();
        await using var read = NewCatalogDb(counter);
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        var results = await reader.SearchAsync("no-such-product-name-zzz");

        Assert.Empty(results);
        Assert.Equal(0, counter.CountMatching("units"));
    }

    [Fact(DisplayName = "ResolveUnitCodesAsync resolves unit ids to display codes directly, omitting unknown ids (plantry-vqa7)")]
    public async Task ResolveUnitCodesAsync_Resolves_By_Unit_Id_Omits_Unknown()
    {
        UnitId poundsId;
        await using (var setup = NewCatalogDb())
        {
            var pounds = Plantry.Catalog.Domain.Unit.Create(_household, "lb", "pounds", Dimension.Mass, 453.592m, isBase: false);
            await setup.Units.AddAsync(pounds);
            await setup.SaveChangesAsync();
            poundsId = pounds.Id;
        }

        await using var read = NewCatalogDb();
        var reader = new MealPlanCatalogProductReaderAdapter(read);

        // Keyed directly by unit id (not joined through a product's default unit) — proves the
        // production override, not just the "ea"-for-everything test stub every other consumer test
        // exercises.
        var codes = await reader.ResolveUnitCodesAsync([_gramsId.Value, poundsId.Value, Guid.NewGuid()]);
        Assert.Equal(2, codes.Count);
        Assert.Equal("g", codes[_gramsId.Value]);
        Assert.Equal("lb", codes[poundsId.Value]);

        Assert.Empty(await reader.ResolveUnitCodesAsync([]));
    }

    private CatalogDbContext NewCatalogDb(QueryCountingInterceptor? counter = null)
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString);
        if (counter is not null) builder.AddInterceptors(counter);

        var ctx = new CatalogDbContext(builder.Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
