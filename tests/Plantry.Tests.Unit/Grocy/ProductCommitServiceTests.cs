using Plantry.Catalog.Domain;
using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="ProductCommitService"/> covering:
/// - Commit order: parents committed before variants
/// - Conversion classification: cross-dimension committed, same-dim/match dropped, same-dim/disagree committed+flagged
/// - SKU synthesis: multi-unit products get a ProductSku
/// - Idempotency: re-running does not duplicate products (duplicate name treated as idempotent)
/// - Products with missing unit crosswalk are skipped
/// </summary>
public sealed class ProductCommitServiceTests
{
    // ──────────── Test infrastructure ────────────────────────────────────────

    private static readonly Guid TestHouseholdId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? HouseholdId => TestHouseholdId;
    }

    private sealed class FakeProductRepository : IProductRepository
    {
        public List<Product> Items { get; } = [];
        public int SaveChangesCalls { get; private set; }

        public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(p => p.Id == id));

        public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(p =>
                p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

        public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

        public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

        public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default) =>
            Task.FromResult(Items.Where(p => ids.Contains(p.Id)).ToList());

        public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
            Task.FromResult(Items.Where(p => p.ParentProductId == parentId).ToList());

        public Task AddAsync(Product product, CancellationToken ct = default)
        {
            Items.Add(product);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitRepository : IUnitRepository
    {
        public List<CatalogUnit> Items { get; } = [];

        public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(u => u.Id == id));

        public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(u =>
                u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

        public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.ToList());

        public Task AddAsync(CatalogUnit unit, CancellationToken ct = default)
        {
            Items.Add(unit);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCategoryRepository : ICategoryRepository
    {
        public List<Category> Items { get; } = [];

        public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(c => c.Id == id));

        public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(c =>
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        public Task<List<Category>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.ToList());

        public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.Where(c => !c.IsArchived).ToList());

        public Task AddAsync(Category category, CancellationToken ct = default)
        {
            Items.Add(category);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeLocationRepository : ILocationRepository
    {
        public List<Location> Items { get; } = [];

        public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(l => l.Id == id));

        public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        public Task<List<Location>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.ToList());

        public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(Items.Where(l => !l.IsArchived).ToList());

        public Task AddAsync(Location location, CancellationToken ct = default)
        {
            Items.Add(location);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ──────────── Builder helpers ─────────────────────────────────────────

    private (ProductCommitService service, FakeProductRepository productRepo, FakeUnitRepository unitRepo)
        BuildService()
    {
        var productRepo  = new FakeProductRepository();
        var unitRepo     = new FakeUnitRepository();
        var categoryRepo = new FakeCategoryRepository();
        var locationRepo = new FakeLocationRepository();
        var clock        = new FakeClock();
        var tenant       = new FakeTenantContext();

        var service = new ProductCommitService(
            productRepo, unitRepo, categoryRepo, locationRepo, clock, tenant);

        return (service, productRepo, unitRepo);
    }

    private static CatalogUnit MakeUnit(Dimension dimension, string code = "ea") =>
        CatalogUnit.Create(Plantry.SharedKernel.HouseholdId.From(TestHouseholdId), code, code, dimension, 1m, true);

    private static ProductStagingRow MakeStagingRow(
        int grocyId,
        string name,
        Guid? defaultUnitId,
        int? parentGrocyId = null,
        StagedProductSku? sku = null) =>
        new()
        {
            GrocyId              = grocyId,
            GrocyName            = name,
            PlantryName          = name,
            GrocyParentProductId = parentGrocyId,
            DefaultUnitId        = defaultUnitId,
            Flags                = parentGrocyId is not null ? ProductStagingFlags.IsVariant : ProductStagingFlags.None,
            SynthesizedSku       = sku,
        };

    private static GrocyManifest EmptyManifest() =>
        new() { ExtractedAt = DateTimeOffset.UtcNow };

    private static GrocyQuantityUnitConversion Conv(
        int id, int from, int to, decimal factor, int? productId = null) =>
        new(id, from, to, factor, productId, null);

    // ──────────── Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_ParentsBeforeVariants_VariantAttachedToParent()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unitId = Guid.NewGuid();
        var unit   = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        // Staging: parent id=1, variant id=2 with parent=1
        var parentRow  = MakeStagingRow(1, "Chicken",       defaultUnitId: unit.Id.Value);
        var variantRow = MakeStagingRow(2, "Chicken Breast", defaultUnitId: unit.Id.Value, parentGrocyId: 1);

        var unitCrosswalk = new Dictionary<int, Guid> { [10] = unit.Id.Value };

        // Use temp directory for crosswalk file
        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var manifest = EmptyManifest();
            var (results, _) = await service.CommitAsync(
                [parentRow, variantRow], manifest, unitCrosswalk, tmpManifest);

            // Both should be committed
            Assert.Equal(2, results.Count);
            Assert.True(results.All(r => r.Success));

            // Parent committed before variant
            Assert.Equal(2, productRepo.Items.Count);
            var parentProduct  = productRepo.Items.Single(p => p.Name == "Chicken");
            var variantProduct = productRepo.Items.Single(p => p.Name == "Chicken Breast");

            // Variant should have ParentProductId set (via MakeVariantCommand)
            Assert.Equal(parentProduct.Id, variantProduct.ParentProductId);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_MissingUnitId_SkipsProduct()
    {
        var (service, productRepo, _) = BuildService();

        // Row with no DefaultUnitId (CrosswalkMissing)
        var row = MakeStagingRow(1, "Unknown Unit Product", defaultUnitId: null);

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], EmptyManifest(), null, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Skipped);
            Assert.Empty(productRepo.Items);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_Idempotent_DuplicateNameNotDuplicated()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row = MakeStagingRow(1, "Milk", defaultUnitId: unit.Id.Value);
        var unitCrosswalk = new Dictionary<int, Guid>();
        var manifest = EmptyManifest();

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            // First run
            await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);
            int countAfterFirst = productRepo.Items.Count;

            // Second run (re-run) — should be idempotent
            await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);
            int countAfterSecond = productRepo.Items.Count;

            Assert.Equal(1, countAfterFirst);
            // Second run: DuplicateProductName → looks up existing → same count
            Assert.Equal(1, countAfterSecond);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_CrossDimensionConversion_Committed()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unitEa = MakeUnit(Dimension.Count, "ea");
        var unitG  = MakeUnit(Dimension.Mass, "g");
        unitRepo.Items.Add(unitEa);
        unitRepo.Items.Add(unitG);

        var eaGrocyId = 2;  // Piece → Count
        var gGrocyId  = 13; // Gram → Mass

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [eaGrocyId] = unitEa.Id.Value,
            [gGrocyId]  = unitG.Id.Value,
        };

        var row = MakeStagingRow(1, "Onion", defaultUnitId: unitEa.Id.Value);

        // Product-specific conversion: 1 Piece (count) = 110 Gram (mass) → cross-dimension
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits = [new GrocyQuantityUnit(eaGrocyId, "Piece", null, null), new GrocyQuantityUnit(gGrocyId, "Gram", null, null)],
            QuantityUnitConversions = [Conv(1, eaGrocyId, gGrocyId, 110m, productId: 1)],
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Success);

            // One conversion committed
            Assert.Single(results[0].Conversions);
            var conv = results[0].Conversions[0];
            Assert.Equal(ProductCommitService.ConversionDisposition.Committed, conv.Disposition);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_SameDimensionConversionMatchesUniversal_Dropped()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unitMl = MakeUnit(Dimension.Volume, "ml");
        var unitL  = MakeUnit(Dimension.Volume, "l");
        unitRepo.Items.Add(unitMl);
        unitRepo.Items.Add(unitL);

        var mlGrocyId = 15;
        var lGrocyId  = 17;

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [mlGrocyId] = unitMl.Id.Value,
            [lGrocyId]  = unitL.Id.Value,
        };

        var row = MakeStagingRow(1, "Olive Oil", defaultUnitId: unitMl.Id.Value);

        // Global conversion: 1 L = 1000 ml
        // Product-specific conversion: same 1 L = 1000 ml → redundant, drop
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits = [new GrocyQuantityUnit(mlGrocyId, "ml", null, null), new GrocyQuantityUnit(lGrocyId, "L", null, null)],
            QuantityUnitConversions =
            [
                Conv(1, lGrocyId, mlGrocyId, 1000m, productId: null),  // global
                Conv(2, lGrocyId, mlGrocyId, 1000m, productId: 1),     // product-specific, matches global
            ],
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Success);

            // The product-specific conversion should be dropped (same-dimension, matches universal)
            Assert.Single(results[0].Conversions);
            Assert.Equal(ProductCommitService.ConversionDisposition.Dropped, results[0].Conversions[0].Disposition);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_SameDimensionConversionDisagreesWithUniversal_CommittedAndFlagged()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unitMl = MakeUnit(Dimension.Volume, "ml");
        var unitCup = MakeUnit(Dimension.Volume, "cup");
        unitRepo.Items.Add(unitMl);
        unitRepo.Items.Add(unitCup);

        var mlGrocyId  = 15;
        var cupGrocyId = 9;

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [mlGrocyId]  = unitMl.Id.Value,
            [cupGrocyId] = unitCup.Id.Value,
        };

        var row = MakeStagingRow(1, "Flour", defaultUnitId: unitMl.Id.Value);

        // Global conversion: 1 Cup = 240 ml
        // Product-specific: 1 Cup = 130 ml (flour is denser — product-specific override, keep + flag)
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits = [new GrocyQuantityUnit(mlGrocyId, "ml", null, null), new GrocyQuantityUnit(cupGrocyId, "Cup", null, null)],
            QuantityUnitConversions =
            [
                Conv(1, cupGrocyId, mlGrocyId, 240m, productId: null), // global
                Conv(2, cupGrocyId, mlGrocyId, 130m, productId: 1),    // product-specific, disagrees
            ],
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Success);

            Assert.Single(results[0].Conversions);
            var conv = results[0].Conversions[0];
            // Same-dimension but disagrees → CommittedFlag
            Assert.Equal(ProductCommitService.ConversionDisposition.CommittedFlag, conv.Disposition);
            Assert.NotNull(conv.Note);
            Assert.Contains("disagrees", conv.Note, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_GlobalConversion_NotCommittedAsProductConversion()
    {
        // Global conversions (ProductId == null) should not produce ProductConversion rows
        var (service, productRepo, unitRepo) = BuildService();

        var unitMl = MakeUnit(Dimension.Volume, "ml");
        var unitL  = MakeUnit(Dimension.Volume, "l");
        unitRepo.Items.Add(unitMl);
        unitRepo.Items.Add(unitL);

        var mlGrocyId = 15;
        var lGrocyId  = 17;

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [mlGrocyId] = unitMl.Id.Value,
            [lGrocyId]  = unitL.Id.Value,
        };

        var row = MakeStagingRow(1, "Water", defaultUnitId: unitMl.Id.Value);

        // Only a global conversion — no product-specific one
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits = [new GrocyQuantityUnit(mlGrocyId, "ml", null, null), new GrocyQuantityUnit(lGrocyId, "L", null, null)],
            QuantityUnitConversions = [Conv(1, lGrocyId, mlGrocyId, 1000m, productId: null)], // global only
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Success);
            // No product-specific conversions for this product → no conversion results
            Assert.Empty(results[0].Conversions);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_MultiUnitProduct_SkuCommitted()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unitEa = MakeUnit(Dimension.Count, "ea");
        var unitMl = MakeUnit(Dimension.Volume, "ml");
        unitRepo.Items.Add(unitEa);
        unitRepo.Items.Add(unitMl);

        var sku = new StagedProductSku
        {
            Label             = "Bottle",
            SizeQuantity      = 500m,
            SizeUnitGrocyId   = 15,
            SizeUnitPlantryId = unitMl.Id.Value,
        };

        var row = MakeStagingRow(1, "Juice", defaultUnitId: unitMl.Id.Value, sku: sku);
        row.Flags |= ProductStagingFlags.IsMultiUnit;

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [15] = unitMl.Id.Value,
            [2]  = unitEa.Id.Value,
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, _) = await service.CommitAsync([row], EmptyManifest(), unitCrosswalk, tmpManifest);

            Assert.Single(results);
            Assert.True(results[0].Success);

            // SKU was committed
            Assert.NotNull(results[0].Sku);
            Assert.True(results[0].Sku!.Success);

            var product = productRepo.Items.Single();
            Assert.Single(product.Skus);
            Assert.Equal("Bottle", product.Skus[0].Label);
            Assert.Equal(500m, product.Skus[0].SizeQuantity);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_WritesCrosswalkFile()
    {
        var (service, _, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row = MakeStagingRow(1, "Eggs", defaultUnitId: unit.Id.Value);
        var unitCrosswalk = new Dictionary<int, Guid>();

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, crosswalkPath) = await service.CommitAsync(
                [row], EmptyManifest(), unitCrosswalk, tmpManifest);

            // Crosswalk file written
            Assert.True(File.Exists(crosswalkPath));

            // And can be re-read
            var loaded = await ProductCrosswalk.TryReadAsync(crosswalkPath);
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Mappings);
            Assert.True(loaded.Mappings.ContainsKey("1"));
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_ReRun_CrosswalkFileUpdatedIdempotently()
    {
        // On re-run, the crosswalk from the first run is loaded so products with the same
        // grocy_id map to the same plantry_id on subsequent runs.
        var (service, productRepo, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row = MakeStagingRow(1, "Eggs", defaultUnitId: unit.Id.Value);
        var unitCrosswalk = new Dictionary<int, Guid>();
        var manifest = EmptyManifest();

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            // First run
            var (_, crosswalkPath1) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);
            var xwalk1 = await ProductCrosswalk.TryReadAsync(crosswalkPath1);
            var plantryId1 = xwalk1!.Mappings["1"];

            // Second run (re-run, product already exists by name)
            var (_, crosswalkPath2) = await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);
            var xwalk2 = await ProductCrosswalk.TryReadAsync(crosswalkPath2);
            var plantryId2 = xwalk2!.Mappings["1"];

            // Same plantry id on both runs
            Assert.Equal(plantryId1, plantryId2);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_ReRun_ConversionsAndSkusNotDuplicated()
    {
        // After a full re-run (product already in crosswalk), conversions and SKUs
        // must not be appended a second time — the idempotency guard must fire.
        var (service, productRepo, unitRepo) = BuildService();

        var unitEa = MakeUnit(Dimension.Count, "ea");
        var unitG  = MakeUnit(Dimension.Mass, "g");
        var unitMl = MakeUnit(Dimension.Volume, "ml");
        unitRepo.Items.Add(unitEa);
        unitRepo.Items.Add(unitG);
        unitRepo.Items.Add(unitMl);

        var eaGrocyId = 2;
        var gGrocyId  = 13;
        var mlGrocyId = 15;

        var unitCrosswalk = new Dictionary<int, Guid>
        {
            [eaGrocyId] = unitEa.Id.Value,
            [gGrocyId]  = unitG.Id.Value,
            [mlGrocyId] = unitMl.Id.Value,
        };

        var sku = new StagedProductSku
        {
            Label             = "Bag",
            SizeQuantity      = 500m,
            SizeUnitGrocyId   = mlGrocyId,
            SizeUnitPlantryId = unitMl.Id.Value,
        };

        var row = MakeStagingRow(1, "Onion", defaultUnitId: unitEa.Id.Value, sku: sku);
        row.Flags |= ProductStagingFlags.IsMultiUnit;

        // Cross-dimension product-specific conversion: 1 ea → 110 g
        var manifest = new GrocyManifest
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            QuantityUnits =
            [
                new GrocyQuantityUnit(eaGrocyId, "Piece", null, null),
                new GrocyQuantityUnit(gGrocyId, "Gram", null, null),
                new GrocyQuantityUnit(mlGrocyId, "ml", null, null),
            ],
            QuantityUnitConversions = [Conv(1, eaGrocyId, gGrocyId, 110m, productId: 1)],
        };

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            // Run 1: products, conversion, and SKU created
            await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            var productAfterRun1 = productRepo.Items.Single();
            Assert.Single(productAfterRun1.Conversions);
            Assert.Single(productAfterRun1.Skus);

            // Run 2: re-run with same data — nothing should be duplicated
            await service.CommitAsync([row], manifest, unitCrosswalk, tmpManifest);

            var productAfterRun2 = productRepo.Items.Single();
            Assert.Single(productAfterRun2.Conversions);
            Assert.Single(productAfterRun2.Skus);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_DroppedProduct_SkippedAndNullWrittenToCrosswalk()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row = MakeStagingRow(1, "Unwanted Product", defaultUnitId: unit.Id.Value);
        row.IsDropped = true;

        var unitCrosswalk = new Dictionary<int, Guid>();

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, crosswalkPath) = await service.CommitAsync(
                [row], EmptyManifest(), unitCrosswalk, tmpManifest);

            // Row is skipped
            Assert.Single(results);
            Assert.True(results[0].Skipped);
            Assert.True(results[0].Success);
            Assert.Null(results[0].PlantryProductId);

            // Not committed to the product repo
            Assert.Empty(productRepo.Items);

            // Crosswalk written with null sentinel entry
            Assert.True(File.Exists(crosswalkPath));
            var loaded = await ProductCrosswalk.TryReadAsync(crosswalkPath);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Mappings.ContainsKey("1"));
            Assert.Null(loaded.Mappings["1"]);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_ReRun_DroppedProductInCrosswalk_StaysSkipped()
    {
        // On re-run, a product with a null entry in the crosswalk should remain skipped
        // even if IsDropped is false (the drop decision is persisted in the crosswalk).
        var (service, productRepo, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row = MakeStagingRow(1, "Was Dropped", defaultUnitId: unit.Id.Value);
        var unitCrosswalk = new Dictionary<int, Guid>();
        var manifest = EmptyManifest();

        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            // Pre-seed a crosswalk with a null entry (simulates prior drop)
            var priorCrosswalk = new ProductCrosswalk
            {
                CommittedAt = DateTimeOffset.UtcNow,
                Mappings    = new Dictionary<string, Guid?> { ["1"] = null },
            };
            await priorCrosswalk.WriteAsync(ProductCrosswalk.ResolvePath(tmpManifest));

            // Re-run with IsDropped = false (e.g. user did not check the box this time)
            var (results, crosswalkPath) = await service.CommitAsync(
                [row], manifest, unitCrosswalk, tmpManifest);

            // Still skipped — crosswalk null entry takes precedence
            Assert.Single(results);
            Assert.True(results[0].Skipped);
            Assert.True(results[0].Success);
            Assert.Empty(productRepo.Items);

            // Crosswalk retains null
            var reloaded = await ProductCrosswalk.TryReadAsync(crosswalkPath);
            Assert.NotNull(reloaded);
            Assert.Null(reloaded!.Mappings["1"]);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    [Fact]
    public async Task CommitAsync_MixedDroppedAndNormal_OnlyNormalProductsCommitted()
    {
        var (service, productRepo, unitRepo) = BuildService();

        var unit = MakeUnit(Dimension.Count, "ea");
        unitRepo.Items.Add(unit);

        var row1 = MakeStagingRow(1, "Keep Me",    defaultUnitId: unit.Id.Value);
        var row2 = MakeStagingRow(2, "Drop Me",    defaultUnitId: unit.Id.Value);
        var row3 = MakeStagingRow(3, "Keep Me Too", defaultUnitId: unit.Id.Value);
        row2.IsDropped = true;

        var unitCrosswalk = new Dictionary<int, Guid>();
        var tmpManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-manifest.json");
        try
        {
            var (results, crosswalkPath) = await service.CommitAsync(
                [row1, row2, row3], EmptyManifest(), unitCrosswalk, tmpManifest);

            Assert.Equal(3, results.Count);

            // rows 1 and 3 committed, row 2 skipped
            Assert.True(results.Single(r => r.GrocyId == 1).Success);
            Assert.False(results.Single(r => r.GrocyId == 1).Skipped);
            Assert.True(results.Single(r => r.GrocyId == 2).Skipped);
            Assert.True(results.Single(r => r.GrocyId == 3).Success);
            Assert.False(results.Single(r => r.GrocyId == 3).Skipped);

            Assert.Equal(2, productRepo.Items.Count);

            // Crosswalk: ids 1 and 3 have GUIDs; id 2 has null
            var loaded = await ProductCrosswalk.TryReadAsync(crosswalkPath);
            Assert.NotNull(loaded!.Mappings["1"]);
            Assert.Null(loaded.Mappings["2"]);
            Assert.NotNull(loaded.Mappings["3"]);
        }
        finally
        {
            TryDeleteCrosswalk(tmpManifest);
        }
    }

    // ──────────── Helpers ─────────────────────────────────────────────────

    private static void TryDeleteCrosswalk(string manifestPath)
    {
        try { File.Delete(ProductCrosswalk.ResolvePath(manifestPath)); } catch { }
    }
}
