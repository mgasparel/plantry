using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Integration.Infrastructure;
using Xunit;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Integration.Catalog;

/// <summary>
/// L3 integration tests proving the Product aggregate (with its SKU/conversion children and
/// composite household-scoped FKs) round-trips through EF against a real Postgres schema —
/// the B4 migration must apply clean and the mapping must hold (PHASE-1-PLAN.md Slice 1, Stage B).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProductRoundTripTests(PostgresFixture db) : IAsyncLifetime
{
    private HouseholdId _household;
    private UnitId _gramsId;
    private UnitId _cupsId;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _household = HouseholdId.New();

        await using var seedDb = NewCatalogDb();
        var grams = CatalogUnit.Create(_household, "g", "grams", Dimension.Mass, 1m, isBase: true);
        var cups = CatalogUnit.Create(_household, "cup", "cups", Dimension.Volume, 240m);
        await seedDb.Units.AddRangeAsync(grams, cups);
        await seedDb.SaveChangesAsync();

        _gramsId = grams.Id;
        _cupsId = cups.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(DisplayName = "Product round-trips with its SKUs and conversions through EF")]
    public async Task Product_RoundTrips_With_Children_Through_EfMapping()
    {
        ProductId productId;

        await using (var db1 = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
            product.AddSku("1 kg bag", 1000m, _gramsId, SystemClock.Instance);
            product.AddConversion(_cupsId, _gramsId, 120m, SystemClock.Instance);
            await db1.Products.AddAsync(product);
            await db1.SaveChangesAsync();
            productId = product.Id;
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Products
            .Include(p => p.Skus)
            .Include(p => p.Conversions)
            .SingleAsync(p => p.Id == productId);

        Assert.Equal("Flour", loaded.Name);
        Assert.Equal(_household, loaded.HouseholdId);

        var sku = Assert.Single(loaded.Skus);
        Assert.Equal("1 kg bag", sku.Label);
        Assert.Equal(1000m, sku.SizeQuantity);
        Assert.Equal(_gramsId, sku.SizeUnitId);
        Assert.Equal(_household, sku.HouseholdId);

        var conversion = Assert.Single(loaded.Conversions);
        Assert.Equal(_cupsId, conversion.FromUnitId);
        Assert.Equal(_gramsId, conversion.ToUnitId);
        Assert.Equal(120m, conversion.Factor);
        Assert.Equal(_household, conversion.HouseholdId);
    }

    [Fact(DisplayName = "Conversion provenance (ai_suggested) round-trips through EF")]
    public async Task Conversion_Source_RoundTrips_Through_EfMapping()
    {
        ProductId productId;

        await using (var db1 = NewCatalogDb())
        {
            var product = Product.Create(_household, "Bananas", _gramsId, SystemClock.Instance);
            product.AddConversion(_cupsId, _gramsId, 5m, SystemClock.Instance, ConversionSource.AiSuggested);
            await db1.Products.AddAsync(product);
            await db1.SaveChangesAsync();
            productId = product.Id;
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);
        var conversion = Assert.Single(loaded.Conversions);
        Assert.Equal(ConversionSource.AiSuggested, conversion.Source);
    }

    [Fact(DisplayName = "Migration backfills a source-less conversion row to user_confirmed")]
    public async Task Migration_Backfills_Existing_Conversions_To_UserConfirmed()
    {
        // Simulate a pre-migration row: insert omitting the `source` column entirely. The migration's
        // column default (defaultValue: "user_confirmed") is what an existing row would have received.
        ProductId productId;
        await using (var db1 = NewCatalogDb())
        {
            var product = Product.Create(_household, "Legacy flour", _gramsId, SystemClock.Instance);
            await db1.Products.AddAsync(product);
            await db1.SaveChangesAsync();
            productId = product.Id;

            await db1.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5})
                """,
                Guid.NewGuid(), _household.Value, productId.Value, _cupsId.Value, _gramsId.Value, 120m);
        }

        await using var db2 = NewCatalogDb();
        var loaded = await db2.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);
        var conversion = Assert.Single(loaded.Conversions);
        Assert.Equal(ConversionSource.UserConfirmed, conversion.Source);
    }

    [Fact(DisplayName = "CHECK constraint rejects an unknown conversion source value")]
    public async Task CheckConstraint_Rejects_Unknown_Source_Value()
    {
        ProductId productId;
        await using var db1 = NewCatalogDb();
        var product = Product.Create(_household, "Constraint flour", _gramsId, SystemClock.Instance);
        await db1.Products.AddAsync(product);
        await db1.SaveChangesAsync();
        productId = product.Id;

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db1.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})
            """,
            Guid.NewGuid(), _household.Value, productId.Value, _cupsId.Value, _gramsId.Value, 120m, "nonsense"));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact(DisplayName = "Self-referencing FK enforces parent and variant share a household")]
    public async Task SelfReferencingForeignKey_Requires_Parent_In_Same_Household()
    {
        await using var db1 = NewCatalogDb();
        var parent = Product.Create(_household, "Milk", _gramsId, SystemClock.Instance);
        await db1.Products.AddAsync(parent);
        await db1.SaveChangesAsync();

        var variant = Product.Create(_household, "Milk (2%)", _gramsId, SystemClock.Instance);
        variant.MakeVariantOf(parent.Id, SystemClock.Instance);
        await db1.Products.AddAsync(variant);
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        var loadedVariant = await db2.Products.SingleAsync(p => p.Id == variant.Id);
        Assert.Equal(parent.Id, loadedVariant.ParentProductId);
    }

    [Fact(DisplayName = "Unique index rejects a duplicate product name within a household")]
    public async Task UniqueIndex_Rejects_Duplicate_Product_Name_Within_Household()
    {
        await using var db1 = NewCatalogDb();
        await db1.Products.AddAsync(Product.Create(_household, "Flour", _gramsId, SystemClock.Instance));
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        await db2.Products.AddAsync(Product.Create(_household, "Flour", _gramsId, SystemClock.Instance));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact(DisplayName = "Archived products are excluded from the active list")]
    public async Task ListActive_Excludes_Archived_Products()
    {
        await using var db1 = NewCatalogDb();
        var active = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
        var archived = Product.Create(_household, "Discontinued spread", _gramsId, SystemClock.Instance);
        archived.Archive(SystemClock.Instance);
        await db1.Products.AddRangeAsync(active, archived);
        await db1.SaveChangesAsync();

        await using var db2 = NewCatalogDb();
        var repo = new ProductRepository(db2);
        var activeProducts = await repo.ListActiveAsync();

        Assert.Contains(activeProducts, p => p.Id == active.Id);
        Assert.DoesNotContain(activeProducts, p => p.Id == archived.Id);
    }

    [Fact(DisplayName = "ListVariants includes archived variants with their conversions eagerly loaded")]
    public async Task ListVariants_Includes_Archived_Variants_With_Conversions()
    {
        ProductId parentId;

        await using (var db1 = NewCatalogDb())
        {
            // Insert the parent first — the self-referencing composite FK means EF can't safely
            // batch parent + variants in one SaveChanges.
            var parent = Product.Create(_household, "Milk", _gramsId, SystemClock.Instance);
            parent.SetHasVariants(true, SystemClock.Instance);
            await db1.Products.AddAsync(parent);
            await db1.SaveChangesAsync();

            var active = Product.Create(_household, "Milk (2%)", _gramsId, SystemClock.Instance);
            active.MakeVariantOf(parent.Id, SystemClock.Instance);

            var archived = Product.Create(_household, "Milk (skim)", _gramsId, SystemClock.Instance);
            archived.MakeVariantOf(parent.Id, SystemClock.Instance);
            archived.AddConversion(_cupsId, _gramsId, 240m, SystemClock.Instance);
            archived.Archive(SystemClock.Instance);

            await db1.Products.AddRangeAsync(active, archived);
            await db1.SaveChangesAsync();
            parentId = parent.Id;
        }

        await using var db2 = NewCatalogDb();
        var repo = new ProductRepository(db2);
        var variants = await repo.ListVariantsAsync(parentId);

        Assert.Equal(2, variants.Count);
        var archivedVariant = Assert.Single(variants, p => p.IsArchived);
        Assert.Single(archivedVariant.Conversions); // conversions eagerly loaded so InheritFrom diffs correctly
    }

    [Fact(DisplayName = "Detaching the last active variant keeps HasVariants true while an archived variant remains")]
    public async Task Detach_Keeps_HasVariants_While_Archived_Variant_Remains()
    {
        ProductId parentId;
        ProductId activeVariantId;

        await using (var db1 = NewCatalogDb())
        {
            // Insert the parent first — the self-referencing composite FK means EF can't safely
            // batch parent + variants in one SaveChanges.
            var parent = Product.Create(_household, "Milk", _gramsId, SystemClock.Instance);
            parent.SetHasVariants(true, SystemClock.Instance);
            await db1.Products.AddAsync(parent);
            await db1.SaveChangesAsync();

            var active = Product.Create(_household, "Milk (2%)", _gramsId, SystemClock.Instance);
            active.MakeVariantOf(parent.Id, SystemClock.Instance);

            var archived = Product.Create(_household, "Milk (skim)", _gramsId, SystemClock.Instance);
            archived.MakeVariantOf(parent.Id, SystemClock.Instance);
            archived.Archive(SystemClock.Instance);

            await db1.Products.AddRangeAsync(active, archived);
            await db1.SaveChangesAsync();
            parentId = parent.Id;
            activeVariantId = active.Id;
        }

        await using (var db2 = NewCatalogDb())
        {
            var repo = new ProductRepository(db2);
            var result = await new DetachProductFromParentCommand(activeVariantId, repo, SystemClock.Instance).ExecuteAsync();
            Assert.True(result.IsSuccess);
        }

        // The only remaining variant is archived, but it still points at the parent — so the parent
        // must stay flagged as a parent (it would otherwise wrongly become stock-holding).
        await using var db3 = NewCatalogDb();
        var reloadedParent = await db3.Products.SingleAsync(p => p.Id == parentId);
        Assert.True(reloadedParent.HasVariants);
    }

    // ── ListByIdsAsync (plantry-ubqb: batch product resolution for the Intake Session detail line
    // grid, no eager-loading) ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ListByIdsAsync: empty input returns an empty result")]
    public async Task ListByIdsAsync_EmptyInput_ReturnsEmpty()
    {
        await using var db2 = NewCatalogDb();
        var repo = new ProductRepository(db2);

        var result = await repo.ListByIdsAsync([]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ListByIdsAsync: returns only the ids that exist, mixed with unknown ids")]
    public async Task ListByIdsAsync_MixedFoundAndMissingIds_ReturnsOnlyFound()
    {
        ProductId flourId;
        await using (var db1 = NewCatalogDb())
        {
            var flour = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
            await db1.Products.AddAsync(flour);
            await db1.SaveChangesAsync();
            flourId = flour.Id;
        }

        var unknownId = ProductId.From(Guid.CreateVersion7());

        await using var db2 = NewCatalogDb();
        var repo = new ProductRepository(db2);
        var result = await repo.ListByIdsAsync([flourId, unknownId]);

        var found = Assert.Single(result);
        Assert.Equal(flourId, found.Id);
        Assert.Equal("Flour", found.Name);
    }

    private DbContextOptions<CatalogDbContext> CatalogOptions() =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.ConnectionString).Options;

    private CatalogDbContext NewCatalogDb()
    {
        var ctx = new CatalogDbContext(CatalogOptions());
        ctx.SetHouseholdId(_household.Value);
        return ctx;
    }
}
