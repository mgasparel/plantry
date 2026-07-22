using Npgsql;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Contract for the page-wide cross-schema read model for the Meal Planner week page (ADR-021).
/// Extracted as an interface so the test harness can replace the real SQL implementation with
/// an in-memory fake without spawning a live database connection.
/// </summary>
public interface IMealPlanWeekReadModel
{
    /// <inheritdoc cref="MealPlanWeekReadModel.LoadAsync"/>
    Task<WeekBag> LoadAsync(
        IReadOnlyList<Guid> recipeIds,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default);
}

/// <summary>
/// Page-wide cross-schema read model for the Meal Planner week page (ADR-021).
///
/// Loads all raw inputs for a week's meals in a small, flat set of queries whose count is
/// independent of meal / dish / ingredient count — single-digit to low-teens regardless of
/// page size. Cross-schema SQL uses fully-qualified schema names. Runs on an RLS-armed
/// connection so Postgres RLS policies (ADR-008) isolate every table regardless of owning
/// context, bypassing per-context EF HasQueryFilter by design.
///
/// Read-only and one-directional: never writes. Only the page depends on the producing-context
/// tables — never the reverse. Output shape mirrors what API composition would return
/// post-split (gather ids + compose), so the eventual extraction fallback is a mechanical
/// refactor (ADR-021 §splittability).
///
/// Lives in Plantry.Web (the composition root) — the one project that legitimately references
/// every context (ADR-021 rule 3).
/// </summary>
public sealed class MealPlanWeekReadModel(
    string connectionString,
    ITenantContext tenant) : IMealPlanWeekReadModel
{
    /// <summary>
    /// Loads the full week's raw inputs for a given set of recipe and product ids gathered
    /// from the already-loaded planned meals. Returns a <see cref="WeekBag"/> keyed for O(1)
    /// lookup by the domain services.
    ///
    /// The caller is responsible for providing the recipe and product ids drawn from the week's
    /// planned dishes; this method loads the supporting data for those ids in bulk.
    /// </summary>
    /// <param name="recipeIds">All distinct recipe ids referenced by any planned dish this week.</param>
    /// <param name="productIds">All distinct product ids referenced by any planned dish this week (for product-type dishes).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WeekBag> LoadAsync(
        IReadOnlyList<Guid> recipeIds,
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);


        // Arm RLS on this connection so every table is household-isolated via Postgres policy.
        // Mirrors the HouseholdRlsConnectionInterceptor (ADR-008): always set the GUC — even when
        // there is no tenant, write an empty string so a pooled connection can never inherit a
        // previous tenant's app.household_id. Uses parameterized set_config (no string interpolation).
        await using var armCmd = conn.CreateCommand();
        armCmd.CommandText = "SELECT set_config('app.household_id', @household_id, false)";
        var hidParam = armCmd.CreateParameter();
        hidParam.ParameterName = "household_id";
        hidParam.Value = tenant.HouseholdId?.ToString() ?? string.Empty;
        armCmd.Parameters.Add(hidParam);
        await armCmd.ExecuteNonQueryAsync(ct);

        // ── Query 1: Week meals + dishes (mealplanning) ─────────────────────────
        // Loaded by the caller (MealPlanRepository) via EF — the plan aggregate is already
        // in-hand before this method is called. This read model supplies the enrichment inputs;
        // the planned meals themselves are loaded through the domain aggregate path.

        // ── Query 2: Recipes + ingredients (cross-schema: recipes) ───────────────
        // One query loads all recipe rows and their ingredient children for every recipe on
        // the page. Uses fully-qualified schema names per ADR-021.
        var recipes = new Dictionary<Guid, RecipeFact>();
        var ingredientsByRecipe = new Dictionary<Guid, List<IngredientFact>>();

        if (recipeIds.Count > 0)
        {
            await LoadRecipesAsync(conn, recipeIds, recipes, ingredientsByRecipe, ct);
        }

        // Collect all product ids from recipe ingredients (union with explicit product dishes).
        var allProductIds = new HashSet<Guid>(productIds);
        foreach (var ing in ingredientsByRecipe.Values.SelectMany(x => x))
            allProductIds.Add(ing.ProductId);

        var allProductIdList = allProductIds.ToList();

        // ── Query 3: Products + conversions (catalog) ────────────────────────────
        // Loads product facts (name, track_stock, default_unit_id, parent/variant tree) and
        // per-product unit conversions in two batched queries.
        var products = new Dictionary<Guid, ProductFact>();
        var conversionsByProduct = new Dictionary<Guid, List<ConversionFact>>();

        if (allProductIdList.Count > 0)
        {
            await LoadProductsAsync(conn, allProductIdList, products, ct);
            await LoadConversionsAsync(conn, allProductIdList, conversionsByProduct, ct);
        }

        // ── Query 4: Units (catalog) ─────────────────────────────────────────────
        // Cacheable: all units for this household. One query regardless of ingredient count.
        var units = new Dictionary<Guid, UnitFact>();
        await LoadUnitsAsync(conn, units, ct);

        // ── Query 5: Stock by product (inventory) ────────────────────────────────
        // Batched: one query loads all active lots for all tracked product ids, aggregating
        // soonest expiry in SQL and summing quantity per product per unit in the app layer.
        // Variant product ids (children of parent products) must be included so DM-19 rollup works.
        var variantProductIds = CollectVariantIds(allProductIdList, products);
        var stockProductIds = allProductIds.Union(variantProductIds).ToList();
        var stockByProduct = new Dictionary<Guid, StockFact>();

        if (stockProductIds.Count > 0)
        {
            await LoadStockAsync(conn, stockProductIds, stockByProduct, ct);
        }

        // ── Query 6: Latest price per product (pricing) ─────────────────────────
        // DISTINCT ON (product_id) ORDER BY product_id, observed_at DESC — one row per product.
        var latestPriceByProduct = new Dictionary<Guid, PriceFact>();

        if (allProductIdList.Count > 0)
        {
            await LoadLatestPricesAsync(conn, allProductIdList, latestPriceByProduct, ct);
        }

        // Cast the mutable accumulation dictionaries to the read-only bag shape.
        // No copy — the inner lists are already complete; the Dictionary<K, List<V>> is
        // compatible with IReadOnlyDictionary<K, IReadOnlyList<V>> via covariant casts.
        return new WeekBag(
            recipes,
            ingredientsByRecipe.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<IngredientFact>)kvp.Value),
            products,
            conversionsByProduct.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<ConversionFact>)kvp.Value),
            units,
            stockByProduct,
            latestPriceByProduct);
    }

    // ── private loaders ──────────────────────────────────────────────────────────────────────────

    private static async Task LoadRecipesAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Guid> recipeIds,
        Dictionary<Guid, RecipeFact> recipes,
        Dictionary<Guid, List<IngredientFact>> ingredientsByRecipe,
        CancellationToken ct)
    {
        // One query: JOIN recipe with its ingredient children for the given recipe ids.
        // LEFT JOIN so recipes with zero ingredients are still returned (name display).
        // Uses fully-qualified schema names (ADR-021).
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                r.recipe_id,
                r.name,
                r.default_servings,
                i.ingredient_id,
                i.product_id       AS ing_product_id,
                i.quantity         AS ing_quantity,
                i.unit_id          AS ing_unit_id,
                i.ordinal          AS ing_ordinal
            FROM recipes.recipe r
            LEFT JOIN recipes.recipe_ingredient i ON i.recipe_id = r.recipe_id
            WHERE r.recipe_id = ANY(@ids)
              AND r.archived_at IS NULL
            ORDER BY r.recipe_id, i.ordinal
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "ids";
        param.Value = recipeIds.ToArray();
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var recipeId = reader.GetGuid(0);

            if (!recipes.ContainsKey(recipeId))
            {
                recipes[recipeId] = new RecipeFact(
                    recipeId,
                    reader.GetString(1),
                    reader.GetInt32(2));
            }

            // ingredient_id is null for recipes with no ingredients (LEFT JOIN).
            if (!reader.IsDBNull(3))
            {
                var ingredientId = reader.GetGuid(3);
                var productId = reader.GetGuid(4);
                var quantity = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
                var unitId = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6);
                var ordinal = reader.GetInt32(7);

                if (!ingredientsByRecipe.TryGetValue(recipeId, out var list))
                {
                    list = [];
                    ingredientsByRecipe[recipeId] = list;
                }

                list.Add(new IngredientFact(ingredientId, recipeId, productId, quantity, unitId, ordinal));
            }
        }
    }

    private static async Task LoadProductsAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Guid> productIds,
        Dictionary<Guid, ProductFact> products,
        CancellationToken ct)
    {
        // Load products + depth-1 parent/variant tree for the given ids.
        // Two passes: (1) load the requested ids; (2) load variant children of any parent products.
        // This matches the DM-19 rollup pattern in FulfillmentService.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.id,
                p.name,
                p.track_stock,
                p.default_unit_id,
                p.parent_product_id,
                p.has_variants,
                p.archived_at
            FROM catalog.products p
            WHERE p.id = ANY(@ids)
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "ids";
        param.Value = productIds.ToArray();
        cmd.Parameters.Add(param);

        var parentIds = new List<Guid>();

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var name = reader.GetString(1);
                var trackStock = reader.GetBoolean(2);
                var defaultUnitId = reader.GetGuid(3);
                var parentProductId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
                var hasVariants = reader.GetBoolean(5);
                var archived = !reader.IsDBNull(6);

                products[id] = new ProductFact(
                    id, name, trackStock, defaultUnitId,
                    parentProductId, hasVariants, Archived: archived,
                    VariantProductIds: []);

                if (hasVariants)
                    parentIds.Add(id);
            }
        }

        // Load variant children for parent products so DM-19 rollup can sum across them.
        if (parentIds.Count > 0)
        {
            await using var varCmd = conn.CreateCommand();
            varCmd.CommandText = """
                SELECT
                    p.id,
                    p.name,
                    p.track_stock,
                    p.default_unit_id,
                    p.parent_product_id,
                    p.has_variants,
                    p.archived_at
                FROM catalog.products p
                WHERE p.parent_product_id = ANY(@parentIds)
                  AND p.archived_at IS NULL
                """;
            var varParam = varCmd.CreateParameter();
            varParam.ParameterName = "parentIds";
            varParam.Value = parentIds.ToArray();
            varCmd.Parameters.Add(varParam);

            var variantsByParent = new Dictionary<Guid, List<Guid>>();
            await using (var varReader = await varCmd.ExecuteReaderAsync(ct))
            {
                while (await varReader.ReadAsync(ct))
                {
                    var variantId = varReader.GetGuid(0);
                    var variantName = varReader.GetString(1);
                    var variantTrackStock = varReader.GetBoolean(2);
                    var variantDefaultUnitId = varReader.GetGuid(3);
                    var variantParentId = varReader.IsDBNull(4) ? (Guid?)null : varReader.GetGuid(4);
                    var variantHasVariants = varReader.GetBoolean(5);
                    var variantArchived = !varReader.IsDBNull(6);

                    if (!products.ContainsKey(variantId))
                    {
                        products[variantId] = new ProductFact(
                            variantId, variantName, variantTrackStock, variantDefaultUnitId,
                            variantParentId, variantHasVariants, Archived: variantArchived,
                            VariantProductIds: []);
                    }

                    if (variantParentId.HasValue)
                    {
                        if (!variantsByParent.TryGetValue(variantParentId.Value, out var siblings))
                        {
                            siblings = [];
                            variantsByParent[variantParentId.Value] = siblings;
                        }
                        siblings.Add(variantId);
                    }
                }
            }

            // Patch variant ids into parent product facts.
            foreach (var (parentId, variantIds) in variantsByParent)
            {
                if (products.TryGetValue(parentId, out var parent))
                    products[parentId] = parent with { VariantProductIds = variantIds };
            }
        }
    }

    private static async Task LoadConversionsAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Guid> productIds,
        Dictionary<Guid, List<ConversionFact>> conversionsByProduct,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.product_id,
                c.from_unit_id,
                c.to_unit_id,
                c.factor
            FROM catalog.product_conversions c
            WHERE c.product_id = ANY(@ids)
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "ids";
        param.Value = productIds.ToArray();
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var productId = reader.GetGuid(0);
            var fromUnitId = reader.GetGuid(1);
            var toUnitId = reader.GetGuid(2);
            var factor = reader.GetDecimal(3);

            if (!conversionsByProduct.TryGetValue(productId, out var list))
            {
                list = [];
                conversionsByProduct[productId] = list;
            }

            list.Add(new ConversionFact(productId, fromUnitId, toUnitId, factor));
        }
    }

    private static async Task LoadUnitsAsync(
        NpgsqlConnection conn,
        Dictionary<Guid, UnitFact> units,
        CancellationToken ct)
    {
        // Load all units for this household — cacheable; small row count.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                u.id,
                u.symbol,
                u.name,
                u.dimension,
                u.factor_to_base,
                u.is_base
            FROM catalog.units u
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var code = reader.GetString(1);
            var name = reader.GetString(2);
            var dimension = reader.GetString(3);
            var factorToBase = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
            var isBase = reader.GetBoolean(5);

            units[id] = new UnitFact(id, code, name, dimension, factorToBase, isBase);
        }
    }

    private static async Task LoadStockAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Guid> productIds,
        Dictionary<Guid, StockFact> stockByProduct,
        CancellationToken ct)
    {
        // One query: aggregate active lots (depleted_at IS NULL) per product.
        // Returns total quantity per (product_id, unit_id) and soonest expiry across lots.
        // The domain services (FulfillmentService) convert across units using the loaded
        // conversions — SQL only aggregates, never converts.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                e.product_id,
                e.unit_id,
                SUM(e.quantity)                   AS total_qty,
                MIN(e.expiry_date)                AS soonest_expiry
            FROM inventory.stock_entry e
            WHERE e.product_id = ANY(@ids)
              AND e.depleted_at IS NULL
              AND e.quantity > 0
            GROUP BY e.product_id, e.unit_id
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "ids";
        param.Value = productIds.ToArray();
        cmd.Parameters.Add(param);

        // Accumulate: a product may have lots in multiple units — gather all (unit → qty) pairs
        // for each product. The domain converter then handles unit unification.
        var lotsByProduct = new Dictionary<Guid, List<StockLotFact>>();
        var soonestByProduct = new Dictionary<Guid, DateOnly?>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var productId = reader.GetGuid(0);
            var unitId = reader.GetGuid(1);
            var totalQty = reader.GetDecimal(2);
            DateOnly? soonestExpiry = reader.IsDBNull(3)
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(3));

            if (!lotsByProduct.TryGetValue(productId, out var lots))
            {
                lots = [];
                lotsByProduct[productId] = lots;
            }

            lots.Add(new StockLotFact(productId, unitId, totalQty));

            // Track soonest expiry across all unit-groups for this product.
            if (!soonestByProduct.TryGetValue(productId, out var current))
                soonestByProduct[productId] = soonestExpiry;
            else if (soonestExpiry.HasValue && (!current.HasValue || soonestExpiry.Value < current.Value))
                soonestByProduct[productId] = soonestExpiry;
        }

        foreach (var (productId, lots) in lotsByProduct)
        {
            soonestByProduct.TryGetValue(productId, out var soonest);
            stockByProduct[productId] = new StockFact(productId, lots, soonest);
        }
    }

    private static async Task LoadLatestPricesAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Guid> productIds,
        Dictionary<Guid, PriceFact> latestPriceByProduct,
        CancellationToken ct)
    {
        // DISTINCT ON (product_id) — Postgres extension: one row per product, latest by observed_at.
        // Equivalent to the per-product LatestForProductAsync but batched for all products at once
        // (including the superseded_by_id IS NULL filter, ADR-023 A7 — an amending row copies the
        // original's observed_at, so without this filter DISTINCT ON's arbitrary tie-break can surface
        // an amended-away row, and for any row superseded by a later-observed live row this always would).
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT ON (p.product_id)
                p.product_id,
                p.price,
                p.quantity,
                p.unit_id,
                p.unit_price,
                p.observed_at
            FROM pricing.price_observation p
            WHERE p.product_id = ANY(@ids)
                AND p.superseded_by_id IS NULL
            ORDER BY p.product_id, p.observed_at DESC
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "ids";
        param.Value = productIds.ToArray();
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var productId = reader.GetGuid(0);
            var price = reader.GetDecimal(1);
            var quantity = reader.GetDecimal(2);
            var unitId = reader.GetGuid(3);
            var unitPrice = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
            var observedAt = reader.GetDateTime(5);

            latestPriceByProduct[productId] = new PriceFact(
                productId, price, quantity, unitId, unitPrice, observedAt);
        }
    }

    // ── private helpers ──────────────────────────────────────────────────────────────────────────

    private static IEnumerable<Guid> CollectVariantIds(
        IReadOnlyList<Guid> productIds,
        IReadOnlyDictionary<Guid, ProductFact> products)
    {
        foreach (var id in productIds)
        {
            if (products.TryGetValue(id, out var p) && p.HasVariants)
            {
                foreach (var variantId in p.VariantProductIds)
                    yield return variantId;
            }
        }
    }
}

// ── Data bag ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Flat in-memory bag of raw inputs for one meal-plan week's enrichment computation.
/// All dictionaries are keyed for O(1) lookup by the domain services.
///
/// Read-only after construction — no mutation, no writes. The domain services
/// (FulfillmentService / CostingService) run the math over this bag without issuing
/// any further round-trips (ADR-021 rule 1).
/// </summary>
public sealed class WeekBag(
    IReadOnlyDictionary<Guid, RecipeFact> recipes,
    IReadOnlyDictionary<Guid, IReadOnlyList<IngredientFact>> ingredientsByRecipe,
    IReadOnlyDictionary<Guid, ProductFact> products,
    IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> conversionsByProduct,
    IReadOnlyDictionary<Guid, UnitFact> units,
    IReadOnlyDictionary<Guid, StockFact> stockByProduct,
    IReadOnlyDictionary<Guid, PriceFact> latestPriceByProduct)
{
    /// <summary>Recipes loaded this week, keyed by recipe_id.</summary>
    public IReadOnlyDictionary<Guid, RecipeFact> Recipes { get; } = recipes;

    /// <summary>Ingredients per recipe, in ordinal order, keyed by recipe_id.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<IngredientFact>> IngredientsByRecipe { get; } = ingredientsByRecipe;

    /// <summary>Product facts (catalog) for every product referenced by any ingredient or dish, keyed by product_id.</summary>
    public IReadOnlyDictionary<Guid, ProductFact> Products { get; } = products;

    /// <summary>Per-product unit conversions (catalog), keyed by product_id.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> ConversionsByProduct { get; } = conversionsByProduct;

    /// <summary>All household units (catalog), keyed by unit_id. Cacheable: one query per request.</summary>
    public IReadOnlyDictionary<Guid, UnitFact> Units { get; } = units;

    /// <summary>Aggregated stock snapshot per product (inventory), keyed by product_id. Only includes products with active lots.</summary>
    public IReadOnlyDictionary<Guid, StockFact> StockByProduct { get; } = stockByProduct;

    /// <summary>Latest purchase-price observation per product (pricing), keyed by product_id.</summary>
    public IReadOnlyDictionary<Guid, PriceFact> LatestPriceByProduct { get; } = latestPriceByProduct;

    // ── O(1) lookup helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>Returns the recipe fact, or null when the recipe is not in this bag.</summary>
    public RecipeFact? GetRecipe(Guid recipeId) =>
        Recipes.TryGetValue(recipeId, out var r) ? r : null;

    /// <summary>Returns ingredients for a recipe, or an empty list when none are loaded.</summary>
    public IReadOnlyList<IngredientFact> GetIngredients(Guid recipeId) =>
        IngredientsByRecipe.TryGetValue(recipeId, out var list) ? list : [];

    /// <summary>Returns the product fact, or null when the product is not in this bag.</summary>
    public ProductFact? GetProduct(Guid productId) =>
        Products.TryGetValue(productId, out var p) ? p : null;

    /// <summary>Returns conversions for a product, or an empty list when none are loaded.</summary>
    public IReadOnlyList<ConversionFact> GetConversions(Guid productId) =>
        ConversionsByProduct.TryGetValue(productId, out var list) ? list : [];

    /// <summary>Returns the unit fact, or null when the unit is not in this bag.</summary>
    public UnitFact? GetUnit(Guid unitId) =>
        Units.TryGetValue(unitId, out var u) ? u : null;

    /// <summary>Returns the stock snapshot for a product, or null when there is no stock on hand.</summary>
    public StockFact? GetStock(Guid productId) =>
        StockByProduct.TryGetValue(productId, out var s) ? s : null;

    /// <summary>Returns the latest price observation for a product, or null when no price history exists.</summary>
    public PriceFact? GetLatestPrice(Guid productId) =>
        LatestPriceByProduct.TryGetValue(productId, out var p) ? p : null;
}

// ── Fact records ─────────────────────────────────────────────────────────────────────────────────

/// <summary>Recipe display facts loaded from <c>recipes.recipe</c>.</summary>
public sealed record RecipeFact(
    Guid RecipeId,
    string Name,
    int DefaultServings);

/// <summary>
/// One ingredient row from <c>recipes.recipe_ingredient</c>.
/// Null Quantity/UnitId means "to taste" (untracked staple, C12).
/// </summary>
public sealed record IngredientFact(
    Guid IngredientId,
    Guid RecipeId,
    Guid ProductId,
    decimal? Quantity,
    Guid? UnitId,
    int Ordinal);

/// <summary>
/// Product facts from <c>catalog.products</c>.
/// Includes the depth-1 parent/variant tree so FulfillmentService can roll up variant stock (DM-19).
/// </summary>
public sealed record ProductFact(
    Guid ProductId,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId,
    Guid? ParentProductId,
    bool HasVariants,
    bool Archived,
    IReadOnlyList<Guid> VariantProductIds);

/// <summary>One unit conversion from <c>catalog.product_conversions</c>.</summary>
public sealed record ConversionFact(
    Guid ProductId,
    Guid FromUnitId,
    Guid ToUnitId,
    decimal Factor);

/// <summary>Unit display facts from <c>catalog.units</c>.</summary>
public sealed record UnitFact(
    Guid UnitId,
    string Code,
    string Name,
    string Dimension,
    decimal? FactorToBase,
    bool IsBase);

/// <summary>
/// Aggregated stock snapshot for one product from <c>inventory.stock_entry</c>.
/// Lots may be expressed in different units — the domain converter handles unification (DM-19).
/// </summary>
public sealed record StockFact(
    Guid ProductId,
    IReadOnlyList<StockLotFact> Lots,
    DateOnly? SoonestExpiry)
{
    /// <summary>True when any active lot exists for this product.</summary>
    public bool HasStock => Lots.Count > 0;
}

/// <summary>One aggregated lot group (product + unit) from the inventory stock query.</summary>
public sealed record StockLotFact(
    Guid ProductId,
    Guid UnitId,
    decimal TotalQuantity);

/// <summary>
/// Latest purchase-price observation for one product from <c>pricing.price_observation</c>.
/// Mirrors <see cref="Plantry.MealPlanning.Application.MealPlanPricePoint"/> — a separate flat
/// record per ADR-021 (no port dependency in this read model).
/// </summary>
public sealed record PriceFact(
    Guid ProductId,
    decimal Price,
    decimal Quantity,
    Guid UnitId,
    decimal? UnitPrice,
    DateTime ObservedAt);
