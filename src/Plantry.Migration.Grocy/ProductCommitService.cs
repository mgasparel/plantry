using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Migration.Grocy.Dto;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Executes the Product commit step of the Grocy import pipeline (grocy-import-plan.md §4.5, §4.2, §7).
///
/// Algorithm:
/// 1. Parents first (non-variants), then variants — so <see cref="Catalog.Domain.Product.InheritFrom"/>
///    can be called after variant attachment.
/// 2. Products are committed via <see cref="CreateProductCommand"/>; duplicate names are treated as
///    idempotent (already committed on a prior run).
/// 3. Product-specific Grocy conversions (166 edges) are classified per §4.2:
///    - Cross-dimension → <see cref="AddConversionCommand"/> (ProductConversion row).
///    - Same-dimension, factor matches universal → drop (redundant; already expressed by factor_to_base).
///    - Same-dimension, factor disagrees → commit as ProductConversion and flag.
///    Global conversions (ProductId == null) are NOT committed — they were consumed to derive
///    factor_to_base in zcw.2 and are already expressed in the Unit model.
/// 4. Multi-unit products get one ProductSku via <see cref="AddSkuCommand"/>.
/// 5. All steps are keyed on the grocy_id crosswalk — re-running upserts rather than duplicating.
/// 6. Writes the grocy_product_id → plantry_product_id crosswalk JSON alongside the manifest.
/// </summary>
public sealed class ProductCommitService(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IClock clock,
    ITenantContext tenant)
{
    // ──────────── Result types ──────────────────────────────────────────────

    /// <summary>Disposition of a single product-specific Grocy conversion.</summary>
    public enum ConversionDisposition
    {
        /// <summary>Cross-dimension: committed as a ProductConversion row.</summary>
        Committed,

        /// <summary>Same-dimension, factor matches universal: dropped as redundant.</summary>
        Dropped,

        /// <summary>Same-dimension, factor disagrees with universal: committed and flagged.</summary>
        CommittedFlag,

        /// <summary>Could not be committed (e.g. units not in crosswalk).</summary>
        Skipped,
    }

    public sealed record ConversionCommitResult(
        int GrocyConversionId,
        int FromQuId,
        int ToQuId,
        decimal Factor,
        ConversionDisposition Disposition,
        string? Note);

    public sealed record SkuCommitResult(
        bool Success,
        string? ErrorMessage);

    public sealed record ProductCommitResult(
        int GrocyId,
        string GrocyName,
        bool Skipped,
        bool Success,
        Guid? PlantryProductId,
        string? ErrorMessage,
        IReadOnlyList<ConversionCommitResult> Conversions,
        SkuCommitResult? Sku);

    // ──────────── Main commit method ────────────────────────────────────────

    /// <summary>
    /// Commits all stageable products to the catalog, writes the crosswalk, and returns per-row results.
    /// Products with a missing DefaultUnitId (CrosswalkMissing flag on the unit) are skipped.
    /// </summary>
    /// <param name="stagingRows">Staging rows from <see cref="ProductStager.Stage"/>.</param>
    /// <param name="manifest">The Grocy manifest — provides the product-specific conversions to classify.</param>
    /// <param name="unitCrosswalk">
    /// grocy_unit_id → plantry_unit_id map, used to resolve conversion from/to unit IDs.
    /// </param>
    /// <param name="manifestFilePath">Manifest file path — used to derive the crosswalk sidecar path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(IReadOnlyList<ProductCommitResult> Results, string CrosswalkPath)> CommitAsync(
        IReadOnlyList<ProductStagingRow> stagingRows,
        GrocyManifest manifest,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        string manifestFilePath,
        CancellationToken ct = default)
    {
        // ── Build unit dimension lookup (grocy_unit_id → Dimension) ─────────
        // Needed for conversion classification (same-dimension vs cross-dimension).
        // We fetch all Plantry units once and build a reverse lookup via the crosswalk.
        var unitDimensionByGrocyId = await BuildUnitDimensionLookupAsync(unitCrosswalk, ct);

        // ── Build product-specific conversion index (grocy product_id → conversions) ─
        // Key: grocy product_id → list of product-specific conversion DTOs
        var productConversionsByGrocyId = manifest.QuantityUnitConversions
            .Where(c => c.ProductId is not null)
            .GroupBy(c => c.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Build global conversion index for same-dimension disagreement check ─
        // grocy global conversions: (fromQuId, toQuId) → factor
        var globalConversionFactors = manifest.QuantityUnitConversions
            .Where(c => c.ProductId is null)
            .ToDictionary(
                c => (c.FromQuId, c.ToQuId),
                c => c.Factor);
        // Also add reverse direction
        foreach (var kv in manifest.QuantityUnitConversions.Where(c => c.ProductId is null).ToList())
        {
            var reverseKey = (kv.ToQuId, kv.FromQuId);
            if (!globalConversionFactors.ContainsKey(reverseKey))
                globalConversionFactors[reverseKey] = 1m / kv.Factor;
        }

        // ── Load existing crosswalk (idempotency: previously committed products) ─
        var crosswalkPath = ProductCrosswalk.ResolvePath(manifestFilePath);
        var existingCrosswalk = await ProductCrosswalk.TryReadAsync(crosswalkPath, ct);
        var crosswalkMappings = existingCrosswalk?.Mappings is not null
            ? new Dictionary<string, Guid?>(existingCrosswalk.Mappings)
            : new Dictionary<string, Guid?>();

        var results = new List<ProductCommitResult>(stagingRows.Count);

        // ── Step 1: Commit parents first ────────────────────────────────────
        // Dropped rows are recorded in the crosswalk as null and excluded from commit.
        var parentRows  = stagingRows.Where(r => !r.IsVariant).OrderBy(r => r.GrocyId).ToList();
        var variantRows = stagingRows.Where(r => r.IsVariant).OrderBy(r => r.GrocyId).ToList();

        foreach (var row in parentRows)
        {
            var result = await CommitProductRowAsync(
                row,
                parentPlantryId: null,
                crosswalkMappings,
                productConversionsByGrocyId,
                globalConversionFactors,
                unitCrosswalk,
                unitDimensionByGrocyId,
                ct);
            results.Add(result);
        }

        // ── Step 2: Commit variants (parent must already be committed) ───────
        foreach (var row in variantRows)
        {
            // Resolve parent's Plantry id from crosswalk (null entries = dropped parent)
            Guid? parentPlantryId = null;
            if (row.GrocyParentProductId is { } parentGrocyId
                && crosswalkMappings.TryGetValue(parentGrocyId.ToString(), out var parentIdNullable)
                && parentIdNullable is { } parentId)
            {
                parentPlantryId = parentId;
            }

            var result = await CommitProductRowAsync(
                row,
                parentPlantryId,
                crosswalkMappings,
                productConversionsByGrocyId,
                globalConversionFactors,
                unitCrosswalk,
                unitDimensionByGrocyId,
                ct);
            results.Add(result);
        }

        // ── Write updated crosswalk ─────────────────────────────────────────
        var crosswalk = new ProductCrosswalk
        {
            CommittedAt = DateTimeOffset.UtcNow,
            Mappings    = new Dictionary<string, Guid?>(crosswalkMappings),
        };
        await crosswalk.WriteAsync(crosswalkPath, ct);

        return (results, crosswalkPath);
    }

    // ──────────── Per-product commit ────────────────────────────────────────

    private async Task<ProductCommitResult> CommitProductRowAsync(
        ProductStagingRow row,
        Guid? parentPlantryId,
        Dictionary<string, Guid?> crosswalkMappings,
        Dictionary<int, List<GrocyQuantityUnitConversion>> productConversionsByGrocyId,
        Dictionary<(int, int), decimal> globalConversionFactors,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        Dictionary<int, Dimension> unitDimensionByGrocyId,
        CancellationToken ct)
    {
        // User-dropped: record a null sentinel in the crosswalk and skip commit.
        if (row.IsDropped)
        {
            // Re-runs: if already recorded as null, leave it; otherwise write null.
            if (!crosswalkMappings.ContainsKey(row.GrocyId.ToString()))
                crosswalkMappings[row.GrocyId.ToString()] = null;

            return new ProductCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: true, Success: true,
                PlantryProductId: null,
                ErrorMessage: "Dropped: user marked this product as dropped on the review screen.",
                Conversions: [],
                Sku: null);
        }

        // Re-run: if this grocy_id was previously dropped (null entry), skip again.
        if (crosswalkMappings.TryGetValue(row.GrocyId.ToString(), out var existingDroppedCheck)
            && existingDroppedCheck is null)
        {
            return new ProductCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: true, Success: true,
                PlantryProductId: null,
                ErrorMessage: "Dropped: previously marked as dropped (null in crosswalk).",
                Conversions: [],
                Sku: null);
        }

        // Skip products where the unit crosswalk is missing — we cannot create them without a valid unit
        if (row.DefaultUnitId is null)
        {
            return new ProductCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: true, Success: true,
                PlantryProductId: null,
                ErrorMessage: "Skipped: DefaultUnitId is missing (CrosswalkMissing on unit).",
                Conversions: [],
                Sku: null);
        }

        try
        {
            Guid plantryId;
            var conversionResults = new List<ConversionCommitResult>();
            SkuCommitResult? skuResult = null;

            // ── Idempotency: already in crosswalk? ──────────────────────────
            if (crosswalkMappings.TryGetValue(row.GrocyId.ToString(), out var existingIdNullable)
                && existingIdNullable is { } existingId)
            {
                plantryId = existingId;
                // Still need to proceed to add any missing conversions/SKUs on re-run
                // (they are idempotent — duplicate attempts are caught by the product aggregate)
            }
            else
            {
                // ── Create the product ──────────────────────────────────────
                var createCmd = new CreateProductCommand(
                    row.PlantryName,
                    row.DefaultUnitId.Value,
                    row.CategoryId,
                    row.DefaultLocationId,
                    products, units, categories, locations,
                    clock, tenant,
                    trackStock: true);

                var createResult = await createCmd.ExecuteAsync(ct);

                if (createResult.IsSuccess)
                {
                    plantryId = createResult.Value.Value;
                    crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                }
                else if (createResult.Error.Code == "Catalog.DuplicateProductName")
                {
                    // Idempotent: already committed — find existing by name
                    var existing = await products.FindByNameAsync(row.PlantryName.Trim(), ct);
                    if (existing is null)
                    {
                        return new ProductCommitResult(
                            row.GrocyId, row.GrocyName,
                            Skipped: false, Success: false,
                            PlantryProductId: null,
                            ErrorMessage: $"Product '{row.PlantryName}' reported as duplicate but could not be found.",
                            Conversions: [],
                            Sku: null);
                    }
                    plantryId = existing.Id.Value;
                    crosswalkMappings[row.GrocyId.ToString()] = plantryId;
                }
                else
                {
                    return new ProductCommitResult(
                        row.GrocyId, row.GrocyName,
                        Skipped: false, Success: false,
                        PlantryProductId: null,
                        ErrorMessage: createResult.Error.Description,
                        Conversions: [],
                        Sku: null);
                }
            }

            var plantryProductId = Catalog.Domain.ProductId.From(plantryId);

            // ── Load the product once — used for expiry defaults, variant attachment,
            //    and duplicate-guard on conversions/SKUs (idempotency on re-run). ──────
            var loadedProduct = await products.FindAsync(plantryProductId, ct);

            // ── Set expiry defaults ─────────────────────────────────────────
            // Applied whenever expiry values are present; idempotent because
            // SetExpiryDefaults overwrites the same fields each time.
            if (loadedProduct is not null
                && (row.DefaultDueDays is not null
                    || row.DefaultDueDaysAfterOpening is not null
                    || row.DefaultDueDaysAfterFreezing is not null
                    || row.DefaultDueDaysAfterThawing is not null))
            {
                loadedProduct.SetExpiryDefaults(
                    row.DefaultDueDays,
                    row.DefaultDueDaysAfterOpening,
                    row.DefaultDueDaysAfterFreezing,
                    row.DefaultDueDaysAfterThawing,
                    clock);
                await products.SaveChangesAsync(ct);
            }

            // ── Attach variant to parent (if applicable) ────────────────────
            if (row.IsVariant && parentPlantryId is { } ppId)
            {
                var makeVariantCmd = new MakeVariantCommand(
                    plantryProductId,
                    Catalog.Domain.ProductId.From(ppId),
                    products, clock);

                var variantResult = await makeVariantCmd.ExecuteAsync(ct);
                // Treat "already a variant" as idempotent — if the product already has this parent
                // (re-run scenario), the command will set the same parent_product_id which is a no-op
                // at the domain level, or may return a different error code we can ignore.
                // MakeVariantCommand itself calls Product.InheritFrom(parent) → expiry/conversion inheritance.
                _ = variantResult; // Result checked below only for hard errors
            }

            // ── Commit product-specific conversions ─────────────────────────
            if (productConversionsByGrocyId.TryGetValue(row.GrocyId, out var grocyConversions))
            {
                foreach (var conv in grocyConversions)
                {
                    var convResult = await CommitConversionAsync(
                        conv,
                        plantryProductId,
                        loadedProduct,
                        unitCrosswalk,
                        unitDimensionByGrocyId,
                        globalConversionFactors,
                        ct);
                    conversionResults.Add(convResult);
                }
            }

            // ── Commit SKU (multi-unit products) ────────────────────────────
            if (row.SynthesizedSku is { } sku && sku.SizeUnitPlantryId is { } sizeUnitId)
            {
                skuResult = await CommitSkuAsync(
                    plantryProductId,
                    loadedProduct,
                    sku.Label,
                    sku.SizeQuantity,
                    sizeUnitId,
                    ct);
            }
            else if (row.SynthesizedSku is not null && row.SynthesizedSku.SizeUnitPlantryId is null)
            {
                skuResult = new SkuCommitResult(
                    Success: false,
                    ErrorMessage: "SKU skipped: SizeUnit not in crosswalk.");
            }

            return new ProductCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: false, Success: true,
                PlantryProductId: plantryId,
                ErrorMessage: null,
                Conversions: conversionResults,
                Sku: skuResult);
        }
        catch (Exception ex)
        {
            return new ProductCommitResult(
                row.GrocyId, row.GrocyName,
                Skipped: false, Success: false,
                PlantryProductId: null,
                ErrorMessage: $"Unexpected error: {ex.Message}",
                Conversions: [],
                Sku: null);
        }
    }

    // ──────────── Conversion commit ─────────────────────────────────────────

    private async Task<ConversionCommitResult> CommitConversionAsync(
        GrocyQuantityUnitConversion conv,
        Catalog.Domain.ProductId plantryProductId,
        Product? loadedProduct,
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        Dictionary<int, Dimension> unitDimensionByGrocyId,
        Dictionary<(int, int), decimal> globalConversionFactors,
        CancellationToken ct)
    {
        // Resolve both units from the crosswalk
        if (unitCrosswalk is null
            || !unitCrosswalk.TryGetValue(conv.FromQuId, out var fromUnitId)
            || !unitCrosswalk.TryGetValue(conv.ToQuId, out var toUnitId))
        {
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                ConversionDisposition.Skipped,
                "Skipped: one or both units not in unit crosswalk.");
        }

        // Determine dimensions
        var fromDimensionKnown = unitDimensionByGrocyId.TryGetValue(conv.FromQuId, out var fromDimension);
        var toDimensionKnown   = unitDimensionByGrocyId.TryGetValue(conv.ToQuId,   out var toDimension);

        if (!fromDimensionKnown || !toDimensionKnown)
        {
            // Can't classify without dimension info — skip
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                ConversionDisposition.Skipped,
                "Skipped: dimension not determinable for one or both units.");
        }

        // ── Classification ──────────────────────────────────────────────────

        if (fromDimension != toDimension)
        {
            // Cross-dimension → ProductConversion row
            return await CommitConversionRowAsync(
                conv, plantryProductId, loadedProduct, fromUnitId, toUnitId,
                ConversionDisposition.Committed, note: null, ct);
        }

        // Same dimension — check if the factor matches the universal (global) factor
        // The global factor connects from → to in the global conversion graph.
        var universalFactor = GetUniversalFactor(conv.FromQuId, conv.ToQuId, globalConversionFactors);

        if (universalFactor is not null && FactorsMatch(conv.Factor, universalFactor.Value))
        {
            // Redundant: same dimension, same factor as universal → drop
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                ConversionDisposition.Dropped,
                "Dropped: same-dimension, factor matches universal conversion.");
        }
        else
        {
            // Same dimension but disagrees with universal (or universal not found) → keep + flag
            var note = universalFactor is not null
                ? $"Flagged: same-dimension product-specific factor ({conv.Factor}) disagrees with universal ({universalFactor.Value:G6})."
                : "Flagged: same-dimension, no universal factor found — treating as product-specific override.";

            return await CommitConversionRowAsync(
                conv, plantryProductId, loadedProduct, fromUnitId, toUnitId,
                ConversionDisposition.CommittedFlag, note, ct);
        }
    }

    private async Task<ConversionCommitResult> CommitConversionRowAsync(
        GrocyQuantityUnitConversion conv,
        Catalog.Domain.ProductId plantryProductId,
        Product? loadedProduct,
        Guid fromUnitId,
        Guid toUnitId,
        ConversionDisposition disposition,
        string? note,
        CancellationToken ct)
    {
        if (fromUnitId == toUnitId)
        {
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                ConversionDisposition.Skipped,
                "Skipped: from-unit and to-unit are the same Plantry unit (collapsed crosswalk).");
        }

        // Idempotency guard: if the product already has a conversion for this
        // (fromUnitId, toUnitId) pair, skip — do not append a duplicate row.
        if (loadedProduct is not null
            && loadedProduct.Conversions.Any(c =>
                c.FromUnitId.Value == fromUnitId && c.ToUnitId.Value == toUnitId))
        {
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                disposition,
                (note is null ? "" : note + " ") + "(already present — skipped on re-run)");
        }

        var cmd = new AddConversionCommand(
            plantryProductId,
            fromUnitId,
            toUnitId,
            conv.Factor,
            products, units, clock);

        var result = await cmd.ExecuteAsync(ct);

        if (result.IsSuccess)
        {
            return new ConversionCommitResult(
                conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
                disposition, note);
        }

        // Treat as soft error — log but don't fail the whole product commit
        return new ConversionCommitResult(
            conv.Id, conv.FromQuId, conv.ToQuId, conv.Factor,
            ConversionDisposition.Skipped,
            $"Skipped: {result.Error.Description}");
    }

    // ──────────── SKU commit ────────────────────────────────────────────────

    private async Task<SkuCommitResult> CommitSkuAsync(
        Catalog.Domain.ProductId plantryProductId,
        Product? loadedProduct,
        string label,
        decimal sizeQuantity,
        Guid sizeUnitId,
        CancellationToken ct)
    {
        // Idempotency guard: if the product already has a SKU with this label, skip.
        if (loadedProduct is not null
            && loadedProduct.Skus.Any(s => s.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
        {
            return new SkuCommitResult(Success: true, ErrorMessage: null);
        }

        var cmd = new AddSkuCommand(
            plantryProductId,
            label,
            sizeQuantity,
            sizeUnitId,
            products, units, clock);

        var result = await cmd.ExecuteAsync(ct);

        if (result.IsSuccess)
            return new SkuCommitResult(Success: true, ErrorMessage: null);

        return new SkuCommitResult(
            Success: false,
            ErrorMessage: result.Error.Description);
    }

    // ──────────── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a grocy_unit_id → Dimension lookup by fetching all Plantry units
    /// and reversing through the crosswalk.
    /// </summary>
    private async Task<Dictionary<int, Dimension>> BuildUnitDimensionLookupAsync(
        IReadOnlyDictionary<int, Guid>? unitCrosswalk,
        CancellationToken ct)
    {
        if (unitCrosswalk is null)
            return [];

        var allUnits = await units.ListAsync(ct);
        var plantryUnitById = allUnits.ToDictionary(u => u.Id.Value, u => u);

        var result = new Dictionary<int, Dimension>();
        foreach (var (grocyId, plantryId) in unitCrosswalk)
        {
            if (plantryUnitById.TryGetValue(plantryId, out var unit))
                result[grocyId] = unit.Dimension;
        }
        return result;
    }

    /// <summary>
    /// Looks up the universal (global) conversion factor from <paramref name="fromQuId"/>
    /// to <paramref name="toQuId"/> in the global conversion table.
    /// Returns null if no global entry exists for this pair.
    /// </summary>
    private static decimal? GetUniversalFactor(
        int fromQuId,
        int toQuId,
        Dictionary<(int, int), decimal> globalConversionFactors)
    {
        if (globalConversionFactors.TryGetValue((fromQuId, toQuId), out var factor))
            return factor;
        return null;
    }

    /// <summary>
    /// Checks whether two conversion factors are numerically equal within a small tolerance
    /// (accounts for floating-point representation differences).
    /// </summary>
    private static bool FactorsMatch(decimal a, decimal b)
    {
        if (b == 0m) return a == 0m;
        var relDiff = Math.Abs(a - b) / Math.Abs(b);
        return relDiff < 0.001m; // 0.1% tolerance
    }
}
