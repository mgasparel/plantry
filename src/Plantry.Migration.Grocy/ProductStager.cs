using System.Globalization;
using Plantry.Migration.Grocy.Dto;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Stages all Grocy products from the manifest into <see cref="ProductStagingRow"/> records.
///
/// Algorithm (per grocy-import-plan.md §4.5):
/// 1. Resolve unit, category, and location via the three crosswalk sidecars.
///    Missing crosswalk entries → CrosswalkMissing flag.
/// 2. Remap expiry sentinels: Grocy stores -1 (no default) and 0 (consume immediately)
///    as -1 or 0; both map to null in Plantry.
/// 3. Detect name collisions against existing catalog names (UNIQUE(household_id, name)).
///    Collisions get a disambiguating " (Grocy)" suffix in PlantryName and the NameCollision flag.
/// 4. Flag variants: products with parent_product_id set → IsVariant flag.
/// 5. Flag dropped barcodes: products with rows in ProductBarcodes → HasDroppedBarcode flag.
/// 6. Detect multi-unit products: qu_id_purchase ≠ qu_id_stock AND a product-specific or global
///    conversion exists → IsMultiUnit flag + synthesize a StagedProductSku.
/// </summary>
public static class ProductStager
{
    /// <summary>
    /// Stages all products from the manifest and returns staging rows in Grocy-id order.
    /// </summary>
    /// <param name="manifest">The Grocy manifest (products, units, conversions, barcodes).</param>
    /// <param name="unitCrosswalk">
    /// grocy_unit_id → plantry_unit_id map. Pass null to treat all unit lookups as missing.
    /// </param>
    /// <param name="categoryCrosswalk">
    /// grocy_product_group_id → plantry_category_id map. Pass null to treat all category lookups as missing.
    /// </param>
    /// <param name="locationCrosswalk">
    /// grocy_location_id → plantry_location_id map. Pass null to treat all location lookups as missing.
    /// </param>
    /// <param name="existingProductNames">
    /// Product names already in the household catalog (case-insensitive). Used to detect NameCollision.
    /// Pass an empty set when no products exist yet.
    /// </param>
    public static IReadOnlyList<ProductStagingRow> Stage(
        GrocyManifest manifest,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        IReadOnlyDictionary<int, Guid>? categoryCrosswalk,
        IReadOnlyDictionary<int, Guid>? locationCrosswalk,
        IReadOnlySet<string>? existingProductNames = null)
    {
        // Build lookup tables
        var unitIdToName = manifest.QuantityUnits.ToDictionary(u => u.Id, u => u.Name);
        var productGroupIdToName = manifest.ProductGroups.ToDictionary(g => g.Id, g => g.Name);
        var locationIdToName = manifest.Locations.ToDictionary(l => l.Id, l => l.Name);

        // Products with barcodes
        var productsWithBarcodes = manifest.ProductBarcodes
            .Select(b => b.ProductId)
            .ToHashSet();

        // Build conversion lookup: (product_id_or_null, from_qu_id) → (to_qu_id, factor)
        // We need to find a conversion from qu_id_purchase to qu_id_stock.
        // Product-specific conversions (ProductId != null) take precedence over global (ProductId == null).
        // Key: (productId, fromQuId) → (toQuId, factor)
        var productConversions = BuildConversionIndex(manifest.QuantityUnitConversions);

        // Existing names for collision detection (case-insensitive)
        var existingNames = existingProductNames
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track names already assigned in this staging run (to detect intra-batch collisions)
        var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<ProductStagingRow>(manifest.Products.Count);

        foreach (var product in manifest.Products.OrderBy(p => p.Id))
        {
            var row = new ProductStagingRow
            {
                GrocyId            = product.Id,
                GrocyName          = product.Name,
                GrocyParentProductId = product.ParentProductId,
                GrocyProductGroupId  = product.ProductGroupId,
                GrocyLocationId      = product.LocationId,
                GrocyQuIdStock       = product.QuIdStock,
                GrocyQuIdPurchase    = product.QuIdPurchase,
                CreatedAt            = ParseTimestamp(product.RowCreatedTimestamp),
            };

            // ── 1. Resolve unit ──────────────────────────────────────────────
            var unitKey = product.QuIdStock.ToString();
            if (unitCrosswalk is not null && unitCrosswalk.TryGetValue(product.QuIdStock, out var unitId))
            {
                row.DefaultUnitId = unitId;
            }
            else
            {
                row.Flags |= ProductStagingFlags.CrosswalkMissing;
            }
            row.DefaultUnitName = unitIdToName.TryGetValue(product.QuIdStock, out var un) ? un : null;

            // ── 2. Resolve category ──────────────────────────────────────────
            if (product.ProductGroupId is { } groupId)
            {
                if (categoryCrosswalk is not null && categoryCrosswalk.TryGetValue(groupId, out var catId))
                {
                    row.CategoryId = catId;
                }
                else
                {
                    row.Flags |= ProductStagingFlags.CrosswalkMissing;
                }
                row.CategoryName = productGroupIdToName.TryGetValue(groupId, out var cn) ? cn : null;
            }

            // ── 3. Resolve location ──────────────────────────────────────────
            if (product.LocationId is { } locId)
            {
                if (locationCrosswalk is not null && locationCrosswalk.TryGetValue(locId, out var locationId))
                {
                    row.DefaultLocationId = locationId;
                }
                else
                {
                    row.Flags |= ProductStagingFlags.CrosswalkMissing;
                }
                row.DefaultLocationName = locationIdToName.TryGetValue(locId, out var ln) ? ln : null;
            }

            // ── 4. Remap expiry sentinels ─────────────────────────────────────
            row.DefaultDueDays             = RemapSentinel(product.DefaultBestBeforeDays);
            row.DefaultDueDaysAfterOpening = RemapSentinel(product.DefaultBestBeforeDaysAfterOpen);
            row.DefaultDueDaysAfterFreezing= RemapSentinel(product.DefaultBestBeforeDaysAfterFreezing);
            row.DefaultDueDaysAfterThawing = RemapSentinel(product.DefaultBestBeforeDaysAfterThawing);

            // ── 5. Name collision detection ──────────────────────────────────
            var candidateName = product.Name.Trim();
            if (existingNames.Contains(candidateName) || assignedNames.Contains(candidateName))
            {
                row.Flags |= ProductStagingFlags.NameCollision;
                // Propose a disambiguated name
                candidateName = $"{candidateName} (Grocy)";
            }
            row.PlantryName = candidateName;
            assignedNames.Add(candidateName);

            // ── 6. Variant flag ──────────────────────────────────────────────
            if (product.ParentProductId is not null)
                row.Flags |= ProductStagingFlags.IsVariant;

            // ── 7. Dropped barcode flag ──────────────────────────────────────
            if (productsWithBarcodes.Contains(product.Id))
                row.Flags |= ProductStagingFlags.HasDroppedBarcode;

            // ── 8. Multi-unit detection + SKU synthesis ───────────────────────
            if (product.QuIdPurchase != product.QuIdStock)
            {
                // Look for a conversion from purchase → stock unit.
                // Product-specific conversions (with this product's id) take precedence.
                var conversion = FindConversion(productConversions, product.Id, product.QuIdPurchase, product.QuIdStock);

                if (conversion is not null)
                {
                    row.Flags |= ProductStagingFlags.IsMultiUnit;

                    var purchaseUnitName = unitIdToName.TryGetValue(product.QuIdPurchase, out var pun) ? pun : $"unit-{product.QuIdPurchase}";

                    Guid? sizeUnitPlantryId = null;
                    if (unitCrosswalk is not null && unitCrosswalk.TryGetValue(product.QuIdStock, out var su))
                        sizeUnitPlantryId = su;

                    row.SynthesizedSku = new StagedProductSku
                    {
                        Label             = purchaseUnitName,
                        SizeQuantity      = conversion.Value.Factor,
                        SizeUnitGrocyId   = product.QuIdStock,
                        SizeUnitPlantryId = sizeUnitPlantryId,
                    };
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    // ──────────── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Remaps Grocy expiry sentinels to Plantry's nullable int:
    ///   -1 (no default set in Grocy) → null
    ///    0 (consume immediately) → null (Plantry models "no default" as null)
    /// Positive values pass through unchanged.
    /// </summary>
    public static int? RemapSentinel(int? grocyValue) =>
        grocyValue is null or <= 0 ? null : grocyValue;

    // Sentinel value used as dictionary key for global (product_id IS NULL) conversions.
    // Grocy product ids start at 1, so 0 is a safe sentinel.
    private const int GlobalConversionKey = 0;

    /// <summary>
    /// Builds a nested lookup: (productId_or_sentinel → fromQuId → (toQuId, factor)).
    /// Global conversions (ProductId == null) are stored under key <see cref="GlobalConversionKey"/> (0).
    /// Both the forward (from→to) and reverse (to→from as 1/factor) edges are stored.
    /// </summary>
    private static Dictionary<int, Dictionary<int, (int ToQuId, decimal Factor)>> BuildConversionIndex(
        IReadOnlyList<GrocyQuantityUnitConversion> conversions)
    {
        var index = new Dictionary<int, Dictionary<int, (int, decimal)>>();

        foreach (var conv in conversions)
        {
            var key = conv.ProductId ?? GlobalConversionKey;

            // Forward: from → to
            if (!index.TryGetValue(key, out var byFrom))
            {
                byFrom = [];
                index[key] = byFrom;
            }
            byFrom[conv.FromQuId] = (conv.ToQuId, conv.Factor);

            // Reverse: to → from (factor = 1/factor)
            if (!byFrom.ContainsKey(conv.ToQuId))
                byFrom[conv.ToQuId] = (conv.FromQuId, 1m / conv.Factor);
        }

        return index;
    }

    /// <summary>
    /// Looks for a direct conversion from <paramref name="fromQuId"/> to <paramref name="toQuId"/>.
    /// Product-specific entry (keyed by productId) is checked first; falls back to global (key 0).
    /// Returns null if no conversion exists.
    /// </summary>
    private static (int ToQuId, decimal Factor)? FindConversion(
        Dictionary<int, Dictionary<int, (int ToQuId, decimal Factor)>> index,
        int productId,
        int fromQuId,
        int toQuId)
    {
        // Try product-specific first
        if (index.TryGetValue(productId, out var productSpecific)
            && productSpecific.TryGetValue(fromQuId, out var ps)
            && ps.ToQuId == toQuId)
        {
            return ps;
        }

        // Fall back to global
        if (index.TryGetValue(GlobalConversionKey, out var global)
            && global.TryGetValue(fromQuId, out var g)
            && g.ToQuId == toQuId)
        {
            return g;
        }

        return null;
    }

    /// <summary>
    /// Marks <see cref="ProductStagingRow.IsDropped"/> on every row whose
    /// <see cref="ProductStagingRow.GrocyId"/> appears in <paramref name="droppedIds"/>.
    ///
    /// <para>
    /// Called by the /Import/Products page model to reconcile the two sources of drop state:
    /// <list type="bullet">
    ///   <item>Current-page selections (Alpine-driven hidden inputs, submitted on POST).</item>
    ///   <item>Cross-page selections (carried as <c>droppedIds</c> query-string parameters on GET,
    ///         or as extra hidden form inputs on POST).</item>
    /// </list>
    /// Merging is done at the call site; this method simply stamps the rows given a unified set.
    /// </para>
    ///
    /// <para>Idempotent — calling it multiple times with the same set is safe.</para>
    /// </summary>
    /// <param name="rows">Staging rows produced by <see cref="Stage"/>.</param>
    /// <param name="droppedIds">Unified set of Grocy product IDs to mark as dropped.</param>
    public static void ApplyDrops(IReadOnlyList<ProductStagingRow> rows, IEnumerable<int> droppedIds)
    {
        var droppedSet = new HashSet<int>(droppedIds);
        if (droppedSet.Count == 0)
            return;

        foreach (var row in rows)
        {
            if (droppedSet.Contains(row.GrocyId))
                row.IsDropped = true;
        }
    }

    /// <summary>
    /// Parses Grocy's "YYYY-MM-DD HH:MM:SS" timestamp format. Returns null on failure.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto2))
            return dto2;

        return null;
    }
}
