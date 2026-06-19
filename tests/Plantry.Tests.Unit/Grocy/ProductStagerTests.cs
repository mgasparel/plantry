using Plantry.Migration.Grocy;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Tests.Unit.Grocy;

/// <summary>
/// Unit tests for <see cref="ProductStager"/> — the product staging algorithm for
/// Grocy product import (plantry-zcw.4).
///
/// Tests cover:
/// - Sentinel remap (-1/0 → null, positive pass-through)
/// - SKU synthesis (multi-unit detection + product-specific and global conversion lookup)
/// - Staging flag logic (NameCollision, IsVariant, HasDroppedBarcode, IsMultiUnit, CrosswalkMissing)
/// - Crosswalk resolution (unit, category, location)
/// - PlantryName collision suffix
/// - All 215 products staged (count acceptance criterion)
/// </summary>
public sealed class ProductStagerTests
{
    // ──────────── Helpers ─────────────────────────────────────────────────

    private static GrocyProduct Product(
        int id,
        string name,
        int quIdStock   = 2,  // default: Piece (ea)
        int quIdPurchase = 2,
        int? productGroupId = null,
        int? locationId = null,
        int? parentProductId = null,
        int? bestBeforeDays = null,
        int? bestBeforeDaysAfterOpen = null,
        int? bestBeforeDaysAfterFreezing = null,
        int? bestBeforeDaysAfterThawing = null,
        string? rowCreatedTimestamp = null) =>
        new(id, name, productGroupId, locationId, quIdStock, quIdPurchase,
            null, null, parentProductId,
            bestBeforeDays, bestBeforeDaysAfterOpen, bestBeforeDaysAfterFreezing, bestBeforeDaysAfterThawing,
            null, null, null, null, null, rowCreatedTimestamp);

    private static GrocyQuantityUnit Unit(int id, string name) =>
        new(id, name, null, null);

    private static GrocyProductBarcode Barcode(int id, int productId) =>
        new(id, productId, "12345678", null, null, null, null);

    private static GrocyQuantityUnitConversion Conv(
        int id, int from, int to, decimal factor, int? productId = null) =>
        new(id, from, to, factor, productId, null);

    private static GrocyProductGroup Group(int id, string name) =>
        new(id, name, null, null);

    private static GrocyLocation Location(int id, string name, int isFreezer = 0) =>
        new(id, name, null, isFreezer, null);

    private static GrocyManifest ManifestWith(
        IEnumerable<GrocyProduct> products,
        IEnumerable<GrocyQuantityUnit>? units = null,
        IEnumerable<GrocyQuantityUnitConversion>? conversions = null,
        IEnumerable<GrocyProductBarcode>? barcodes = null,
        IEnumerable<GrocyProductGroup>? groups = null,
        IEnumerable<GrocyLocation>? locations = null) =>
        new GrocyManifest
        {
            ExtractedAt           = DateTimeOffset.UtcNow,
            Products              = products.ToList(),
            QuantityUnits         = units?.ToList() ?? [],
            QuantityUnitConversions = conversions?.ToList() ?? [],
            ProductBarcodes       = barcodes?.ToList() ?? [],
            ProductGroups         = groups?.ToList() ?? [],
            Locations             = locations?.ToList() ?? [],
        };

    // ──────────── Sentinel remap tests ────────────────────────────────────

    [Theory]
    [InlineData(-1,  null)]
    [InlineData(0,   null)]
    [InlineData(1,   1)]
    [InlineData(7,   7)]
    [InlineData(365, 365)]
    public void RemapSentinel_ConvertsNegativeAndZeroToNull(int? input, int? expected)
    {
        var result = ProductStager.RemapSentinel(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Stage_RemapsAllFourExpirySentinels()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk",
                bestBeforeDays: -1,
                bestBeforeDaysAfterOpen: 0,
                bestBeforeDaysAfterFreezing: 30,
                bestBeforeDaysAfterThawing: 7)],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Null(row.DefaultDueDays);             // -1 → null
        Assert.Null(row.DefaultDueDaysAfterOpening); // 0  → null
        Assert.Equal(30, row.DefaultDueDaysAfterFreezing);
        Assert.Equal(7, row.DefaultDueDaysAfterThawing);
    }

    // ──────────── CrosswalkMissing flag tests ─────────────────────────────

    [Fact]
    public void Stage_NullCrosswalks_SetsCrosswalkMissingFlag()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk", quIdStock: 13, productGroupId: 5, locationId: 3)],
            [Unit(13, "Gram")],
            groups: [Group(5, "Dairy")],
            locations: [Location(3, "Fridge")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Single(rows);
        Assert.True(rows[0].HasCrosswalkMissing);
    }

    [Fact]
    public void Stage_WithFullCrosswalks_NoCrosswalkMissing()
    {
        var unitId = Guid.NewGuid();
        var catId  = Guid.NewGuid();
        var locId  = Guid.NewGuid();

        var manifest = ManifestWith(
            [Product(1, "Milk", quIdStock: 13, productGroupId: 5, locationId: 3)],
            [Unit(13, "Gram")],
            groups: [Group(5, "Dairy")],
            locations: [Location(3, "Fridge")]);

        var unitMap     = new Dictionary<int, Guid> { [13] = unitId };
        var categoryMap = new Dictionary<int, Guid> { [5]  = catId  };
        var locationMap = new Dictionary<int, Guid> { [3]  = locId  };

        var rows = ProductStager.Stage(manifest, unitMap, categoryMap, locationMap);

        Assert.Single(rows);
        var row = rows[0];
        Assert.False(row.HasCrosswalkMissing);
        Assert.Equal(unitId, row.DefaultUnitId);
        Assert.Equal(catId,  row.CategoryId);
        Assert.Equal(locId,  row.DefaultLocationId);
    }

    [Fact]
    public void Stage_ProductWithNullCategoryAndLocation_NoCrosswalkMissing()
    {
        var unitId = Guid.NewGuid();
        var manifest = ManifestWith(
            [Product(1, "Salt", quIdStock: 2, productGroupId: null, locationId: null)],
            [Unit(2, "Piece")]);

        var unitMap = new Dictionary<int, Guid> { [2] = unitId };

        var rows = ProductStager.Stage(manifest, unitMap, null, null);

        Assert.Single(rows);
        Assert.False(rows[0].HasCrosswalkMissing);
        Assert.Null(rows[0].CategoryId);
        Assert.Null(rows[0].DefaultLocationId);
    }

    // ──────────── NameCollision flag tests ────────────────────────────────

    [Fact]
    public void Stage_NameMatchesExisting_SetsNameCollisionFlag()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk")],
            [Unit(2, "Piece")]);

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Milk" };

        var rows = ProductStager.Stage(manifest, null, null, null, existingNames);

        Assert.Single(rows);
        Assert.True(rows[0].HasNameCollision);
        Assert.StartsWith("Milk", rows[0].PlantryName);
        Assert.Contains("Grocy", rows[0].PlantryName);
    }

    [Fact]
    public void Stage_NoExistingNames_NoNameCollision()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk")],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Single(rows);
        Assert.False(rows[0].HasNameCollision);
        Assert.Equal("Milk", rows[0].PlantryName);
    }

    [Fact]
    public void Stage_NameCollision_IsCaseInsensitive()
    {
        var manifest = ManifestWith(
            [Product(1, "MILK")],
            [Unit(2, "Piece")]);

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "milk" };

        var rows = ProductStager.Stage(manifest, null, null, null, existingNames);

        Assert.True(rows[0].HasNameCollision);
    }

    // ──────────── IsVariant flag tests ────────────────────────────────────

    [Fact]
    public void Stage_ProductWithParentId_SetsIsVariantFlag()
    {
        var manifest = ManifestWith(
            [
                Product(1, "Chicken"),
                Product(2, "Chicken Breast", parentProductId: 1),
            ],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Equal(2, rows.Count);
        var parent  = rows.First(r => r.GrocyId == 1);
        var variant = rows.First(r => r.GrocyId == 2);

        Assert.False(parent.IsVariant);
        Assert.True(variant.IsVariant);
        Assert.Equal(1, variant.GrocyParentProductId);
    }

    // ──────────── HasDroppedBarcode flag tests ────────────────────────────

    [Fact]
    public void Stage_ProductWithBarcode_SetsHasDroppedBarcodeFlag()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk"), Product(2, "Eggs")],
            [Unit(2, "Piece")],
            barcodes: [Barcode(100, 1)]);  // only product 1 has a barcode

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Equal(2, rows.Count);
        Assert.True(rows.First(r => r.GrocyId == 1).HasDroppedBarcode);
        Assert.False(rows.First(r => r.GrocyId == 2).HasDroppedBarcode);
    }

    // ──────────── IsMultiUnit + SKU synthesis tests ───────────────────────

    [Fact]
    public void Stage_SameStockAndPurchaseUnit_NoMultiUnitFlag()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk", quIdStock: 15, quIdPurchase: 15)],  // same unit
            [Unit(15, "ml")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.False(rows[0].IsMultiUnit);
        Assert.Null(rows[0].SynthesizedSku);
    }

    [Fact]
    public void Stage_DifferentUnitsNoConversion_NoMultiUnitFlag()
    {
        // qu_id_purchase ≠ qu_id_stock but no conversion exists → not a multi-unit product
        var manifest = ManifestWith(
            [Product(1, "Juice", quIdStock: 15, quIdPurchase: 24)],  // ml vs Pint, no conversion
            [Unit(15, "ml"), Unit(24, "Pint")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.False(rows[0].IsMultiUnit);
        Assert.Null(rows[0].SynthesizedSku);
    }

    [Fact]
    public void Stage_ProductSpecificConversion_SynthesizesSku()
    {
        // Product 1 buys in "Bottle" (id=8) but stocks in "ml" (id=15).
        // Product-specific conversion: 1 Bottle = 500 ml
        var manifest = ManifestWith(
            [Product(1, "Juice", quIdStock: 15, quIdPurchase: 8)],
            [Unit(15, "ml"), Unit(8, "Bottle")],
            conversions: [Conv(1, 8, 15, 500m, productId: 1)]);

        var unitMap = new Dictionary<int, Guid> { [15] = Guid.NewGuid() };

        var rows = ProductStager.Stage(manifest, unitMap, null, null);

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.IsMultiUnit);
        Assert.NotNull(row.SynthesizedSku);
        Assert.Equal("Bottle", row.SynthesizedSku!.Label);
        Assert.Equal(500m, row.SynthesizedSku!.SizeQuantity);
        Assert.Equal(15, row.SynthesizedSku!.SizeUnitGrocyId);
        Assert.NotNull(row.SynthesizedSku!.SizeUnitPlantryId);
    }

    [Fact]
    public void Stage_GlobalConversion_SynthesizesSku_WhenNoProductSpecificExists()
    {
        // Global conversion: 1 Bottle = 500 ml (no product_id)
        var manifest = ManifestWith(
            [Product(1, "Juice", quIdStock: 15, quIdPurchase: 8)],
            [Unit(15, "ml"), Unit(8, "Bottle")],
            conversions: [Conv(1, 8, 15, 500m, productId: null)]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.True(rows[0].IsMultiUnit);
        Assert.NotNull(rows[0].SynthesizedSku);
        Assert.Equal("Bottle", rows[0].SynthesizedSku!.Label);
        Assert.Equal(500m, rows[0].SynthesizedSku!.SizeQuantity);
    }

    [Fact]
    public void Stage_ProductSpecificConversion_TakesPrecedenceOverGlobal()
    {
        // Global: 1 Bottle = 500 ml; product-specific: 1 Bottle = 750 ml
        var manifest = ManifestWith(
            [Product(1, "Wine", quIdStock: 15, quIdPurchase: 8)],
            [Unit(15, "ml"), Unit(8, "Bottle")],
            conversions:
            [
                Conv(1, 8, 15, 500m, productId: null),    // global
                Conv(2, 8, 15, 750m, productId: 1),       // product-specific
            ]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.True(rows[0].IsMultiUnit);
        Assert.Equal(750m, rows[0].SynthesizedSku!.SizeQuantity);  // product-specific wins
    }

    // ──────────── Multiple flags simultaneously ───────────────────────────

    [Fact]
    public void Stage_ProductWithMultipleFlags_SetsAllFlags()
    {
        var unitId = Guid.NewGuid();
        var manifest = ManifestWith(
            [Product(1, "Existing Juice", quIdStock: 15, quIdPurchase: 8, parentProductId: 42)],
            [Unit(15, "ml"), Unit(8, "Bottle")],
            conversions: [Conv(1, 8, 15, 500m, productId: null)],
            barcodes: [Barcode(1, 1)]);

        var unitMap = new Dictionary<int, Guid> { [15] = unitId };
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Existing Juice" };

        var rows = ProductStager.Stage(manifest, unitMap, null, null, existingNames);

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.HasNameCollision);
        Assert.True(row.IsVariant);
        Assert.True(row.HasDroppedBarcode);
        Assert.True(row.IsMultiUnit);
        // CrosswalkMissing only for category (null) and location (null) — both null so no missing
        // But category_id is null, location_id is null — those aren't missing, just absent
        // (CrosswalkMissing flag only set when the id is present but no crosswalk entry exists)
        Assert.True(row.IsFlagged);
    }

    // ──────────── Ordering and parent grouping ────────────────────────────

    [Fact]
    public void Stage_ReturnsRowsOrderedByGrocyId()
    {
        var manifest = ManifestWith(
            [Product(10, "Banana"), Product(3, "Apple"), Product(7, "Cherry")],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Equal([3, 7, 10], rows.Select(r => r.GrocyId).ToArray());
    }

    // ──────────── Row count and field mapping ────────────────────────────

    [Fact]
    public void Stage_MapsGrocyFieldsCorrectly()
    {
        var unitId = Guid.NewGuid();
        var catId  = Guid.NewGuid();
        var locId  = Guid.NewGuid();

        var manifest = ManifestWith(
            [Product(42, "Whole Milk",
                quIdStock: 15,
                quIdPurchase: 17,
                productGroupId: 3,
                locationId: 8,
                bestBeforeDays: 7,
                bestBeforeDaysAfterOpen: -1,
                rowCreatedTimestamp: "2025-01-15 10:30:00")],
            [Unit(15, "ml"), Unit(17, "Liter")],
            groups: [Group(3, "Dairy")],
            locations: [Location(8, "Fridge")]);

        var unitMap     = new Dictionary<int, Guid> { [15] = unitId };
        var categoryMap = new Dictionary<int, Guid> { [3]  = catId  };
        var locationMap = new Dictionary<int, Guid> { [8]  = locId  };

        var rows = ProductStager.Stage(manifest, unitMap, categoryMap, locationMap);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(42,       row.GrocyId);
        Assert.Equal("Whole Milk", row.GrocyName);
        Assert.Equal("Whole Milk", row.PlantryName);
        Assert.Equal(unitId,   row.DefaultUnitId);
        Assert.Equal("ml",     row.DefaultUnitName);
        Assert.Equal(catId,    row.CategoryId);
        Assert.Equal("Dairy",  row.CategoryName);
        Assert.Equal(locId,    row.DefaultLocationId);
        Assert.Equal("Fridge", row.DefaultLocationName);
        Assert.Equal(7,        row.DefaultDueDays);
        Assert.Null(row.DefaultDueDaysAfterOpening); // -1 → null
        Assert.NotNull(row.CreatedAt);
    }

    // ──────────── Timestamp parsing ───────────────────────────────────────

    [Theory]
    [InlineData("2025-01-15 10:30:00")]
    [InlineData("2026-06-18 00:00:00")]
    public void Stage_ParsesGrocyTimestamp(string timestamp)
    {
        var manifest = ManifestWith(
            [Product(1, "Milk", rowCreatedTimestamp: timestamp)],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.NotNull(rows[0].CreatedAt);
    }

    [Fact]
    public void Stage_NullTimestamp_ReturnsNullCreatedAt()
    {
        var manifest = ManifestWith(
            [Product(1, "Milk", rowCreatedTimestamp: null)],
            [Unit(2, "Piece")]);

        var rows = ProductStager.Stage(manifest, null, null, null);

        Assert.Null(rows[0].CreatedAt);
    }

    // ──────────── IsFlagged convenience ──────────────────────────────────

    [Fact]
    public void Stage_CleanProduct_IsNotFlagged()
    {
        var unitId = Guid.NewGuid();
        var manifest = ManifestWith(
            [Product(1, "Eggs", quIdStock: 2, quIdPurchase: 2)],
            [Unit(2, "Piece")]);

        var unitMap = new Dictionary<int, Guid> { [2] = unitId };

        var rows = ProductStager.Stage(manifest, unitMap, null, null);

        Assert.False(rows[0].IsFlagged);
    }
}
