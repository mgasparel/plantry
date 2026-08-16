using Npgsql;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Housekeeping;

/// <summary>
/// Contract for the shared cross-schema read model backing Tidy Up's recipe-family detectors — D2
/// (<c>RecipeConversionGapDetector</c>), D5 (<c>RecipeIngredientNoPriceDetector</c>), D7
/// (<c>RecipeLineUntrackedProductDetector</c>). Extracted as an interface so tests can substitute an
/// in-memory fake (mirrors <c>IMealPlanWeekReadModel</c>).
/// </summary>
public interface IRecipeFactsReadModel
{
    /// <inheritdoc cref="RecipeFactsReadModel.LoadAsync"/>
    Task<RecipeFactsBag> LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// Household-wide cross-schema read model for Tidy Up's recipe-family detectors (ADR-021, ADR-024 Phase
/// A). All three recipe detectors scan every one of the household's recipes, so — unlike the Meal
/// Planner week page — there is no caller-supplied id set to narrow by; this loads every recipe, its
/// ingredients, and the catalog/pricing facts those ingredients reference in a small, flat set of
/// queries whose count is independent of recipe/ingredient count.
///
/// Runs on an RLS-armed connection (ADR-008), same as <c>StockFactsReadModel</c>/
/// <c>MealPlanWeekReadModel</c>. Read-only and one-directional. Lives in Plantry.Web (ADR-021 rule 3).
/// </summary>
public sealed class RecipeFactsReadModel(
    string connectionString,
    ITenantContext tenant) : IRecipeFactsReadModel
{
    public async Task<RecipeFactsBag> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var armCmd = conn.CreateCommand())
        {
            armCmd.CommandText = "SELECT set_config('app.household_id', @household_id::text, false)";
            var hidParam = armCmd.CreateParameter();
            hidParam.ParameterName = "household_id";
            hidParam.Value = tenant.HouseholdId ?? Guid.Empty;
            armCmd.Parameters.Add(hidParam);
            await armCmd.ExecuteNonQueryAsync(ct);
        }

        // ── Query 1: every non-archived recipe + its ingredient lines (recipes) ─────────────────────
        var recipes = new Dictionary<Guid, RecipeFact>();
        var ingredientsByRecipe = new Dictionary<Guid, List<RecipeIngredientFact>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    r.recipe_id,
                    r.name,
                    i.ingredient_id,
                    i.product_id  AS ing_product_id,
                    i.quantity    AS ing_quantity,
                    i.unit_id     AS ing_unit_id,
                    i.ordinal     AS ing_ordinal
                FROM recipes.recipe r
                LEFT JOIN recipes.recipe_ingredient i ON i.recipe_id = r.recipe_id
                WHERE r.household_id = @household_id AND r.archived_at IS NULL
                ORDER BY r.recipe_id, i.ordinal
                """;
            cmd.Parameters.AddWithValue("household_id", tenant.HouseholdId ?? Guid.Empty);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var recipeId = reader.GetGuid(0);
                if (!recipes.ContainsKey(recipeId))
                    recipes[recipeId] = new RecipeFact(recipeId, reader.GetString(1));

                if (reader.IsDBNull(2))
                    continue; // recipe with no ingredients (LEFT JOIN)

                var fact = new RecipeIngredientFact(
                    reader.GetGuid(2),
                    recipeId,
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.GetInt32(6));

                if (!ingredientsByRecipe.TryGetValue(recipeId, out var list))
                {
                    list = [];
                    ingredientsByRecipe[recipeId] = list;
                }
                list.Add(fact);
            }
        }

        if (recipes.Count == 0)
            return new RecipeFactsBag(
                recipes,
                new Dictionary<Guid, IReadOnlyList<RecipeIngredientFact>>(),
                new Dictionary<Guid, ProductFact>(),
                new Dictionary<Guid, UnitFact>(),
                new Dictionary<Guid, IReadOnlyList<ConversionFact>>(),
                new Dictionary<Guid, IReadOnlyList<PriceObservationFact>>(),
                new Dictionary<Guid, IReadOnlyList<LiveVariantFact>>());

        var productIds = ingredientsByRecipe.Values
            .SelectMany(x => x)
            .Select(i => i.ProductId)
            .Distinct()
            .ToArray();

        // ── Query 2: products (catalog) — active only. ──────────────────────────────────────────────
        var products = new Dictionary<Guid, ProductFact>();
        if (productIds.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, track_stock, default_unit_id, default_location_id, has_variants
                FROM catalog.products
                WHERE household_id = @household_id AND id = ANY(@ids) AND archived_at IS NULL
                """;
            var householdParam = cmd.CreateParameter();
            householdParam.ParameterName = "household_id";
            householdParam.Value = tenant.HouseholdId ?? Guid.Empty;
            cmd.Parameters.Add(householdParam);
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = productIds;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                products[id] = new ProductFact(
                    id,
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.GetBoolean(5));
            }
        }

        // ── Query 2b: live variants of parent ingredients (catalog) — D5's parent rollup scope ────
        // A parent ingredient is priced via its live (non-archived) direct variants only (DM-19;
        // plantry-i07l rule 2/5). Load the variant children of every parent ingredient product with
        // their own default units, so D5 can convert each variant's observation to the parent's
        // reference unit. Catalog enforces maximum tree depth one — no recursion.
        var variantsByParent = new Dictionary<Guid, List<LiveVariantFact>>();
        var variantIds = new HashSet<Guid>();
        var parentIds = products.Values.Where(p => p.IsParent).Select(p => p.ProductId).ToArray();
        if (parentIds.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, parent_product_id, default_unit_id
                FROM catalog.products
                WHERE household_id = @household_id AND parent_product_id = ANY(@ids) AND archived_at IS NULL
                """;
            var householdParam = cmd.CreateParameter();
            householdParam.ParameterName = "household_id";
            householdParam.Value = tenant.HouseholdId ?? Guid.Empty;
            cmd.Parameters.Add(householdParam);
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = parentIds;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var parentId = reader.GetGuid(1);
                var variantId = reader.GetGuid(0);
                if (!variantsByParent.TryGetValue(parentId, out var list))
                {
                    list = [];
                    variantsByParent[parentId] = list;
                }
                list.Add(new LiveVariantFact(variantId, reader.GetGuid(2)));
                variantIds.Add(variantId);
            }
        }

        // Pricing/conversion refs = ingredient product ids ∪ live variant ids, so a variant's own
        // conversions and observations are loaded alongside its parent's (one batched read, no N+1).
        var allRefs = productIds.Concat(variantIds).Distinct().ToArray();

        // ── Query 3: units (catalog) — all household units. ─────────────────────────────────────────
        var units = new Dictionary<Guid, UnitFact>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, symbol, name, dimension, factor_to_base, is_base
                FROM catalog.units
                WHERE household_id = @household_id
                """;
            cmd.Parameters.AddWithValue("household_id", tenant.HouseholdId ?? Guid.Empty);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                units[id] = new UnitFact(
                    id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.GetBoolean(5));
            }
        }

        // ── Query 4: product conversions (catalog), for D2's conversion-path check. Loaded for the
        // ingredient products AND their live variants so D5's parent rollup can convert each variant
        // observation to the parent's reference unit (ADR-021 rule 1 — no round-trips in the detector).
        var conversionsByProduct = new Dictionary<Guid, List<ConversionFact>>();
        if (allRefs.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT product_id, from_unit_id, to_unit_id, factor
                FROM catalog.product_conversions
                WHERE household_id = @household_id AND product_id = ANY(@ids)
                """;
            cmd.Parameters.AddWithValue("household_id", tenant.HouseholdId ?? Guid.Empty);
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = allRefs;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(0);
                var fact = new ConversionFact(productId, reader.GetGuid(1), reader.GetGuid(2), reader.GetDecimal(3));
                if (!conversionsByProduct.TryGetValue(productId, out var list))
                {
                    list = [];
                    conversionsByProduct[productId] = list;
                }
                list.Add(fact);
            }
        }

        // ── Query 5: usable price observations (pricing) — D5's batch pricing facts. Unlike the
        // retired exact-id existence check (IPriceObservationRepository.ProductIdsWithAnyObservationAsync),
        // this loads only USABLE observations — live (superseded_by_id IS NULL, ADR-023 A7), quantity > 0,
        // and a real (non-empty) unit — for the ingredient products AND their live variant refs, so D5 can
        // evaluate a live-variant rollup (plantry-i07l rule 5) rather than raw existence. A unitless deal
        // (DM-17 writes unit_id = Guid.Empty) and an empty-quantity row have no conversion basis and must
        // not count. Multiple usable rows per ref are kept — D5 needs to know whether ANY one converts.
        var priceObservations = new Dictionary<Guid, List<PriceObservationFact>>();
        if (allRefs.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT product_id, price, quantity, unit_id, unit_price
                FROM pricing.price_observation
                WHERE household_id = @household_id AND product_id = ANY(@ids)
                  AND superseded_by_id IS NULL
                  AND quantity > 0
                  AND unit_id <> '00000000-0000-0000-0000-000000000000'
                """;
            cmd.Parameters.AddWithValue("household_id", tenant.HouseholdId ?? Guid.Empty);
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = allRefs;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var productId = reader.GetGuid(0);
                var fact = new PriceObservationFact(
                    productId,
                    reader.GetDecimal(1),
                    reader.GetDecimal(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4));
                if (!priceObservations.TryGetValue(productId, out var list))
                {
                    list = [];
                    priceObservations[productId] = list;
                }
                list.Add(fact);
            }
        }

        return new RecipeFactsBag(
            recipes,
            ingredientsByRecipe.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<RecipeIngredientFact>)kvp.Value),
            products,
            units,
            conversionsByProduct.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ConversionFact>)kvp.Value),
            priceObservations.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<PriceObservationFact>)kvp.Value),
            variantsByParent.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<LiveVariantFact>)kvp.Value));
    }
}

// ── Data bag ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Flat in-memory bag of raw inputs shared by Tidy Up's recipe-family detectors (D2/D5/D7). Read-only
/// after construction; the detectors run their own pure math over this bag with no further round-trips.
/// </summary>
public sealed class RecipeFactsBag(
    IReadOnlyDictionary<Guid, RecipeFact> recipes,
    IReadOnlyDictionary<Guid, IReadOnlyList<RecipeIngredientFact>> ingredientsByRecipe,
    IReadOnlyDictionary<Guid, ProductFact> products,
    IReadOnlyDictionary<Guid, UnitFact> units,
    IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> conversionsByProduct,
    IReadOnlyDictionary<Guid, IReadOnlyList<PriceObservationFact>> priceObservations,
    IReadOnlyDictionary<Guid, IReadOnlyList<LiveVariantFact>> liveVariantsByParent)
{
    public IReadOnlyDictionary<Guid, RecipeFact> Recipes { get; } = recipes;
    public IReadOnlyDictionary<Guid, IReadOnlyList<RecipeIngredientFact>> IngredientsByRecipe { get; } = ingredientsByRecipe;
    public IReadOnlyDictionary<Guid, ProductFact> Products { get; } = products;
    public IReadOnlyDictionary<Guid, UnitFact> Units { get; } = units;
    public IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> ConversionsByProduct { get; } = conversionsByProduct;

    /// <summary>Usable live (non-superseded, quantity &gt; 0, non-empty unit) price observations keyed by
    /// product id — the ref id being a leaf ingredient or a live variant of a parent (plantry-i07l). D5
    /// decides "has a price" by whether any observation yields a usable, convertible candidate, not by raw
    /// existence.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<PriceObservationFact>> PriceObservations { get; } = priceObservations;

    /// <summary>Live (non-archived) direct variants of each parent ingredient, with each variant's own default
    /// unit — D5's parent rollup scope (DM-19; plantry-i07l rule 2/5).</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<LiveVariantFact>> LiveVariantsByParent { get; } = liveVariantsByParent;

    /// <summary>
    /// Convenience overload for the non-pricing detector tests (D2/D7), which do not load price facts. The
    /// legacy <paramref name="pricedProductIds"/> set is not consulted by D5 any more — D5 reads the richer
    /// <see cref="PriceObservations"/>/<see cref="LiveVariantsByParent"/> facts via the full constructor
    /// (plantry-i07l rule 5) — so this overload maps it to an empty fact list and no variants.
    /// </summary>
    public RecipeFactsBag(
        IReadOnlyDictionary<Guid, RecipeFact> recipes,
        IReadOnlyDictionary<Guid, IReadOnlyList<RecipeIngredientFact>> ingredientsByRecipe,
        IReadOnlyDictionary<Guid, ProductFact> products,
        IReadOnlyDictionary<Guid, UnitFact> units,
        IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> conversionsByProduct,
        IReadOnlySet<Guid> pricedProductIds)
        : this(recipes, ingredientsByRecipe, products, units, conversionsByProduct,
            pricedProductIds.ToDictionary(id => id, id => (IReadOnlyList<PriceObservationFact>)[]),
            new Dictionary<Guid, IReadOnlyList<LiveVariantFact>>())
    {
    }

    public IReadOnlyList<RecipeIngredientFact> GetIngredients(Guid recipeId) =>
        IngredientsByRecipe.TryGetValue(recipeId, out var list) ? list : [];

    /// <summary>Builds the shared unit-conversion delegate over this bag's Units/ConversionsByProduct (see
    /// <c>HousekeepingConversions.BuildConverter</c>).</summary>
    public Func<Guid, decimal, Guid, Guid, Plantry.SharedKernel.Result<decimal>> BuildConverter() =>
        HousekeepingConversions.BuildConverter(Units, ConversionsByProduct);
}

// ── Fact records ─────────────────────────────────────────────────────────────────────────────────

/// <summary>Recipe display facts from <c>recipes.recipe</c>.</summary>
public sealed record RecipeFact(Guid RecipeId, string Name);

/// <summary>
/// One ingredient row from <c>recipes.recipe_ingredient</c>. Null Quantity/UnitId means "to taste"
/// (untracked staple, C12).
/// </summary>
public sealed record RecipeIngredientFact(
    Guid IngredientId,
    Guid RecipeId,
    Guid ProductId,
    decimal? Quantity,
    Guid? UnitId,
    int Ordinal);
