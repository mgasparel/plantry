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

    [Fact(DisplayName = "Nullable Never-expiry overrides round-trip and remain null for an unconfigured product")]
    public async Task Product_NeverExpiryOverrides_RoundTrip_Without_Backfill()
    {
        ProductId unconfiguredId;
        ProductId configuredId;

        await using (var db1 = NewCatalogDb())
        {
            var unconfigured = Product.Create(_household, "Unconfigured yoghurt", _gramsId, SystemClock.Instance);
            var configured = Product.Create(_household, "Configured yoghurt", _gramsId, SystemClock.Instance);
            configured.SetNeverExpiryOverrides(true, false, SystemClock.Instance);

            Assert.Null(unconfigured.NeverExpiresAfterFreezing);
            Assert.Null(unconfigured.NeverExpiresAfterThawing);
            await db1.Products.AddRangeAsync(unconfigured, configured);
            await db1.SaveChangesAsync();
            unconfiguredId = unconfigured.Id;
            configuredId = configured.Id;
        }

        await using var db2 = NewCatalogDb();
        var loadedUnconfigured = await db2.Products.SingleAsync(p => p.Id == unconfiguredId);
        var loadedConfigured = await db2.Products.SingleAsync(p => p.Id == configuredId);

        Assert.Null(loadedUnconfigured.NeverExpiresAfterFreezing);
        Assert.Null(loadedUnconfigured.NeverExpiresAfterThawing);
        Assert.True(loadedConfigured.NeverExpiresAfterFreezing);
        Assert.False(loadedConfigured.NeverExpiresAfterThawing);
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

    // ── Unordered-pair unique index (ADR-022 amendment, plantry-pcfe) ─────────

    [Fact(DisplayName = "Unique expression index rejects a SAME-direction duplicate conversion for an existing pair")]
    public async Task UniqueIndex_Rejects_SameDirection_Duplicate_ConversionPair()
    {
        ProductId productId;
        await using var db1 = NewCatalogDb();
        var product = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
        await db1.Products.AddAsync(product);
        await db1.SaveChangesAsync();
        productId = product.Id;

        await db1.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
            Guid.CreateVersion7(), _household.Value, productId.Value, _cupsId.Value, _gramsId.Value, 120m);

        // Bypasses Product.AddConversion's own in-memory guard on purpose, to prove the DATABASE
        // itself enforces the invariant independently of the domain layer (defense in depth).
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db1.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
            Guid.CreateVersion7(), _household.Value, productId.Value, _cupsId.Value, _gramsId.Value, 100m));

        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    [Fact(DisplayName = "Unique expression index rejects a REVERSE-direction duplicate conversion for an existing pair")]
    public async Task UniqueIndex_Rejects_ReverseDirection_Duplicate_ConversionPair()
    {
        ProductId productId;
        await using var db1 = NewCatalogDb();
        var product = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
        await db1.Products.AddAsync(product);
        await db1.SaveChangesAsync();
        productId = product.Id;

        await db1.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
            Guid.CreateVersion7(), _household.Value, productId.Value, _cupsId.Value, _gramsId.Value, 120m);

        // Same unordered pair {cup, g}, opposite direction — the exact shape rule 4's old
        // directional lookup let through (the ADR-022 hole this ticket closes). The expression
        // index canonicalises via LEAST/GREATEST, so it catches this regardless of direction.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db1.Database.ExecuteSqlRawAsync(
            "INSERT INTO catalog.product_conversions (id, household_id, product_id, from_unit_id, to_unit_id, factor, source) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'user_confirmed')",
            Guid.CreateVersion7(), _household.Value, productId.Value, _gramsId.Value, _cupsId.Value, 1m / 120m));

        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    // ── Replace-on-confirm round-trips through EF (ADR-022 amendment, plantry-pcfe) ──
    //
    // The unique expression index is intentionally absent from CatalogDbContext's model (an
    // expression index cannot be declared via HasIndex — see the comment there), which means EF
    // Core's model-declared-unique-index ordering guarantee does NOT cover it: when
    // Product.AddConversion replaces an existing row for the same unordered pair, the resulting
    // DELETE + INSERT against product_conversions in one SaveChangesAsync falls through to EF's
    // incidental EntityState-based command ordering instead of an index-aware one. These Facts
    // prove that ordering actually puts the DELETE first against the real database — not merely
    // that Product's in-memory list looks right (ProductTests already covers that) or that the raw
    // SQL-level index rejects a naked duplicate insert (the two Facts above) — closing the gap
    // between "the aggregate's invariant is correct" and "persisting it doesn't 23505."

    [Fact(DisplayName = "A same-direction user-confirmed replace round-trips through EF without a unique-index violation")]
    public async Task UserConfirmed_Replace_SameDirection_RoundTrips()
    {
        ProductId productId;
        Guid originalConversionId;

        await using (var db1 = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
            var original = product.AddConversion(_cupsId, _gramsId, 120m, SystemClock.Instance);
            await db1.Products.AddAsync(product);
            await db1.SaveChangesAsync();
            productId = product.Id;
            originalConversionId = original.Id.Value;
        }

        await using (var db2 = NewCatalogDb())
        {
            var loaded = await db2.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

            // Same direction, same unordered pair, re-confirmed with a corrected factor — must
            // replace the existing row in the SAME SaveChangesAsync, not throw 23505.
            loaded.AddConversion(_cupsId, _gramsId, 150m, SystemClock.Instance);
            await db2.SaveChangesAsync();
        }

        await using var db3 = NewCatalogDb();
        var reloaded = await db3.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);
        var surviving = Assert.Single(reloaded.Conversions);
        Assert.Equal(150m, surviving.Factor);
        Assert.NotEqual(originalConversionId, surviving.Id.Value);
    }

    [Fact(DisplayName = "A reverse-direction user-confirmed replace round-trips through EF without a unique-index violation")]
    public async Task UserConfirmed_Replace_ReverseDirection_RoundTrips()
    {
        ProductId productId;
        Guid originalConversionId;

        await using (var db1 = NewCatalogDb())
        {
            var product = Product.Create(_household, "Flour", _gramsId, SystemClock.Instance);
            var original = product.AddConversion(_cupsId, _gramsId, 120m, SystemClock.Instance);
            await db1.Products.AddAsync(product);
            await db1.SaveChangesAsync();
            productId = product.Id;
            originalConversionId = original.Id.Value;
        }

        await using (var db2 = NewCatalogDb())
        {
            var loaded = await db2.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);

            // Reverse direction, same unordered pair {cup, g} — must still replace, not throw.
            loaded.AddConversion(_gramsId, _cupsId, 0.008m, SystemClock.Instance);
            await db2.SaveChangesAsync();
        }

        await using var db3 = NewCatalogDb();
        var reloaded = await db3.Products.Include(p => p.Conversions).SingleAsync(p => p.Id == productId);
        var surviving = Assert.Single(reloaded.Conversions);
        Assert.Equal(_gramsId, surviving.FromUnitId);
        Assert.Equal(_cupsId, surviving.ToUnitId);
        Assert.Equal(0.008m, surviving.Factor);
        Assert.NotEqual(originalConversionId, surviving.Id.Value);
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
