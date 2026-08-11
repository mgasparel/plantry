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
                new HashSet<Guid>());

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

        // ── Query 4: product conversions (catalog), for D2's conversion-path check. ─────────────────
        var conversionsByProduct = new Dictionary<Guid, List<ConversionFact>>();
        if (productIds.Length > 0)
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
            param.Value = productIds;
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

        // ── Query 5: priced product ids (pricing) — D5's batch existence check. Mirrors
        // IPriceObservationRepository.ProductIdsWithAnyObservationAsync: any live (non-superseded,
        // ADR-023 A7) observation of any source counts. ────────────────────────────────────────────
        var pricedProductIds = new HashSet<Guid>();
        if (productIds.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT product_id
                FROM pricing.price_observation
                WHERE household_id = @household_id AND product_id = ANY(@ids) AND superseded_by_id IS NULL
                """;
            cmd.Parameters.AddWithValue("household_id", tenant.HouseholdId ?? Guid.Empty);
            var param = cmd.CreateParameter();
            param.ParameterName = "ids";
            param.Value = productIds;
            cmd.Parameters.Add(param);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                pricedProductIds.Add(reader.GetGuid(0));
        }

        return new RecipeFactsBag(
            recipes,
            ingredientsByRecipe.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<RecipeIngredientFact>)kvp.Value),
            products,
            units,
            conversionsByProduct.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ConversionFact>)kvp.Value),
            pricedProductIds);
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
    IReadOnlySet<Guid> pricedProductIds)
{
    public IReadOnlyDictionary<Guid, RecipeFact> Recipes { get; } = recipes;
    public IReadOnlyDictionary<Guid, IReadOnlyList<RecipeIngredientFact>> IngredientsByRecipe { get; } = ingredientsByRecipe;
    public IReadOnlyDictionary<Guid, ProductFact> Products { get; } = products;
    public IReadOnlyDictionary<Guid, UnitFact> Units { get; } = units;
    public IReadOnlyDictionary<Guid, IReadOnlyList<ConversionFact>> ConversionsByProduct { get; } = conversionsByProduct;

    /// <summary>Product ids with at least one live (non-superseded) price observation of any source — D5's
    /// batch existence check.</summary>
    public IReadOnlySet<Guid> PricedProductIds { get; } = pricedProductIds;

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
