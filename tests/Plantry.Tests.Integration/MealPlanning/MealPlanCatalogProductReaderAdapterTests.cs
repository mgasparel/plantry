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

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(
            new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options);
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
