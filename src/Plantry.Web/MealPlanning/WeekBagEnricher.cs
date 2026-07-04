using Plantry.MealPlanning.Application;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Adapts a <see cref="WeekBag"/> into Recipes domain types and runs the pure
/// <see cref="FulfillmentService.Compute"/> / <see cref="CostingService.Compute"/> overloads
/// (ADR-021 rule 1: SQL fetches data, C# keeps the math).
///
/// Memoizes the <see cref="RecipeDishEnrichment"/> result per (recipeId, servings) so a recipe
/// used in multiple cells is enriched exactly once per request. The enricher is created fresh per
/// LoadWeekAsync call — it is NOT registered in DI; the page creates one and passes it through.
/// </summary>
internal sealed class WeekBagEnricher
{
    private readonly WeekBag _bag;
    private readonly FulfillmentService _fulfillmentService;
    private readonly CostingService _costingService;
    private readonly IClock _clock;
    private readonly int _expiringSoonDays;

    // Memo cache: keyed by (recipeId, servings). Populated on first call, reused on subsequent ones.
    private readonly Dictionary<(Guid RecipeId, int Servings), RecipeDishEnrichment?> _memo = [];

    public WeekBagEnricher(
        WeekBag bag,
        FulfillmentService fulfillmentService,
        CostingService costingService,
        IClock clock,
        int expiringSoonDays)
    {
        _bag = bag;
        _fulfillmentService = fulfillmentService;
        _costingService = costingService;
        _clock = clock;
        _expiringSoonDays = expiringSoonDays;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the recipe display name from the bag, or null when the recipe is not loaded.
    /// </summary>
    public string? GetRecipeName(Guid recipeId) =>
        _bag.GetRecipe(recipeId)?.Name;

    /// <summary>
    /// Computes (or returns memoized) fulfillment+cost enrichment for a recipe dish.
    /// Returns null when the recipe is not in the bag (e.g. archived between load and render).
    /// </summary>
    public RecipeDishEnrichment? Enrich(Guid recipeId, int servings, DateOnly today)
    {
        var key = (recipeId, servings);
        if (_memo.TryGetValue(key, out var cached))
            return cached;

        var result = ComputeEnrichment(recipeId, servings, today);
        _memo[key] = result;
        return result;
    }

    // ── Private compute ───────────────────────────────────────────────────────

    private RecipeDishEnrichment? ComputeEnrichment(Guid recipeId, int servings, DateOnly today)
    {
        var recipeFact = _bag.GetRecipe(recipeId);
        if (recipeFact is null)
            return null;

        var ingredients = _bag.GetIngredients(recipeId);
        // A recipe with no ingredients (e.g. just loaded for name display) has nothing to enrich.
        // Still return a result with 100% (untracked-only convention, C12) so the cell renders.
        // Note: ReplaceIngredients requires at least one ingredient line — we only call it when
        // there are ingredients. When there are none, we synthesize a 100% result directly.
        if (ingredients.Count == 0)
        {
            return new RecipeDishEnrichment(
                FulfillmentPercent: 100,
                TotalCost: null,
                CostIsPartial: false,
                HasExpiringIngredients: false);
        }

        // Build the Recipe domain object from bag facts (no EF round-trip).
        // Recipe.Create + ReplaceIngredients are public APIs; we use SystemClock since the
        // timestamp is irrelevant for a read-only computation.
        var recipe = BuildRecipe(recipeFact, ingredients);

        // Build adapter dictionaries for the pure compute overloads.
        // Converter is built first so BuildStockById can use it when summing multi-unit lots
        // into the product's default unit (matching InventoryStockReaderAdapter behaviour).
        var converter = BuildConverter(recipeId);
        var catalogById = BuildCatalogById(ingredients);
        var stockById = BuildStockById(catalogById, converter);
        var priceById = BuildPriceById(ingredients);

        // Fulfillment (pure — zero round-trips).
        var fulfillment = _fulfillmentService.Compute(recipe, servings, today, catalogById, stockById, converter, _expiringSoonDays);

        // Cost (pure — zero round-trips).
        var cost = _costingService.Compute(recipe, servings, priceById, converter);

        // Map fulfillment lines → percentage (mirrors RecipeReadModelAdapter.GetEnrichmentAsync).
        var trackedLines = fulfillment.Lines
            .Where(l => l.Status != IngredientStatus.Untracked)
            .ToList();

        int pct;
        if (trackedLines.Count == 0)
        {
            // No tracked ingredients → 100% (untracked-only recipe is always cookable, C12).
            pct = 100;
        }
        else
        {
            var inStockCount = trackedLines.Count(l => l.Status == IngredientStatus.InStock);
            pct = (int)Math.Round(100.0 * inStockCount / trackedLines.Count);
        }

        var hasExpiring = fulfillment.Lines.Any(l => l.ExpiresWithinDays.HasValue);

        // TotalCost = CostPerServing.Amount × servings (Amount is per-serving).
        decimal? totalCost = cost.Amount.HasValue ? cost.Amount.Value * servings : null;

        return new RecipeDishEnrichment(
            pct,
            totalCost,
            cost.Completeness == CostCompleteness.Partial,
            hasExpiring);
    }

    // ── Adapter builders ──────────────────────────────────────────────────────

    /// <summary>Builds a transient Recipe domain object from WeekBag facts (no EF).</summary>
    private static Recipe BuildRecipe(RecipeFact recipeFact, IReadOnlyList<IngredientFact> ingredients)
    {
        // Use a sentinel HouseholdId — only the ingredient data matters for pure compute.
        var household = HouseholdId.From(Guid.Empty);
        var recipe = Recipe.Create(household, recipeFact.Name, recipeFact.DefaultServings, SystemClock.Instance).Value;

        var lines = ingredients
            .OrderBy(i => i.Ordinal)
            .Select((i, idx) => new IngredientLine(
                i.ProductId,
                i.Quantity,
                i.UnitId,
                null,
                idx)) // re-number from 0 to satisfy R6 contiguity
            .ToList();

        recipe.ReplaceIngredients(lines, SystemClock.Instance);
        return recipe;
    }

    /// <summary>
    /// Builds the CatalogProduct lookup for the pure FulfillmentService.Compute overload.
    /// Includes the product itself and its variant children when it is a parent (DM-19).
    /// </summary>
    private IReadOnlyDictionary<Guid, CatalogProduct> BuildCatalogById(
        IReadOnlyList<IngredientFact> ingredients)
    {
        var result = new Dictionary<Guid, CatalogProduct>();

        foreach (var ing in ingredients)
        {
            var productFact = _bag.GetProduct(ing.ProductId);
            if (productFact is null) continue;

            AddProductIfAbsent(result, productFact);

            // Include variant children so FulfillmentService can roll up DM-19 parent stock.
            foreach (var variantId in productFact.VariantProductIds)
            {
                var variantFact = _bag.GetProduct(variantId);
                if (variantFact is not null)
                    AddProductIfAbsent(result, variantFact);
            }
        }

        return result;
    }

    private static void AddProductIfAbsent(
        Dictionary<Guid, CatalogProduct> result,
        ProductFact fact)
    {
        if (!result.ContainsKey(fact.ProductId))
        {
            result[fact.ProductId] = new CatalogProduct(
                fact.ProductId,
                fact.Name,
                fact.TrackStock,
                fact.DefaultUnitId,
                fact.ParentProductId,
                IsParent: fact.HasVariants,
                VariantProductIds: fact.VariantProductIds);
        }
    }

    /// <summary>
    /// Builds the ProductStock lookup for the pure FulfillmentService.Compute overload.
    /// Mirrors <c>InventoryStockReaderAdapter.FindStockBatchAsync</c>: each product's
    /// <see cref="ProductStock.AvailableQuantity"/> is the sum of ALL active lots converted
    /// into the product's default unit, with lots that fail conversion contributing 0.
    /// This ensures parity when a product is stocked in multiple units (e.g. 2 kg + 500 g).
    /// </summary>
    private IReadOnlyDictionary<Guid, ProductStock> BuildStockById(
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var result = new Dictionary<Guid, ProductStock>();

        foreach (var (productId, catalogProduct) in catalogById)
        {
            var stockFact = _bag.GetStock(productId);
            if (stockFact is null) continue; // not in stock → omit (FulfillmentService treats absent as zero)

            var defaultUnitId = catalogProduct.DefaultUnitId;

            // Sum ALL lots converted into the product's default unit.
            // Lots that cannot be converted contribute 0 — identical to InventoryStockReaderAdapter.
            var totalAvailable = 0m;
            foreach (var lot in stockFact.Lots)
            {
                if (lot.UnitId == defaultUnitId)
                {
                    totalAvailable += lot.TotalQuantity;
                }
                else
                {
                    var converted = converter(productId, lot.TotalQuantity, lot.UnitId, defaultUnitId);
                    if (converted.IsSuccess)
                        totalAvailable += converted.Value;
                    // Unconvertible lots contribute 0 (same as adapter behaviour).
                }
            }

            if (totalAvailable <= 0m) continue; // No usable stock — omit.

            result[productId] = new ProductStock(
                productId,
                totalAvailable,
                defaultUnitId,
                stockFact.SoonestExpiry);
        }

        return result;
    }

    /// <summary>
    /// Builds the PricePoint lookup for the pure CostingService.Compute overload.
    /// </summary>
    private IReadOnlyDictionary<Guid, PricePoint> BuildPriceById(
        IReadOnlyList<IngredientFact> ingredients)
    {
        var result = new Dictionary<Guid, PricePoint>();

        foreach (var ing in ingredients)
        {
            if (result.ContainsKey(ing.ProductId)) continue;

            var priceFact = _bag.GetLatestPrice(ing.ProductId);
            if (priceFact is null) continue;

            result[ing.ProductId] = new PricePoint(
                priceFact.ProductId,
                priceFact.Price,
                priceFact.Quantity,
                priceFact.UnitId,
                priceFact.UnitPrice);
        }

        return result;
    }

    /// <summary>
    /// Builds the sync converter delegate for the pure compute overloads from the WeekBag units
    /// and product-specific conversions. Mirrors the algorithm in <c>Catalog.Domain.UnitConverter</c>
    /// (same-dimension scaling + product conversion bridges) without allocating EF domain objects.
    /// </summary>
    private Func<Guid, decimal, Guid, Guid, Result<decimal>> BuildConverter(Guid recipeId)
    {
        // Capture bags by reference — closures are cheap, the dictionaries are already loaded.
        var units = _bag.Units;
        var conversionsByProduct = _bag.ConversionsByProduct;

        return (productId, amount, fromUnitId, toUnitId) =>
        {
            // Same unit: identity.
            if (fromUnitId == toUnitId)
                return Result<decimal>.Success(amount);

            // Same-dimension scaling: both units must be in the loaded bag.
            var sameDimFactor = SameDimensionFactor(fromUnitId, toUnitId, units);
            if (sameDimFactor.HasValue)
                return Result<decimal>.Success(amount * sameDimFactor.Value);

            // Product-specific conversions (cross-dimension / density).
            if (conversionsByProduct.TryGetValue(productId, out var productConversions))
            {
                foreach (var conv in productConversions)
                {
                    // Forward direction: from → conv.From → conv.To → to
                    var bridgeIn = SameDimensionFactor(fromUnitId, conv.FromUnitId, units);
                    var bridgeOut = SameDimensionFactor(conv.ToUnitId, toUnitId, units);
                    if (bridgeIn.HasValue && bridgeOut.HasValue)
                        return Result<decimal>.Success(amount * bridgeIn.Value * conv.Factor * bridgeOut.Value);
                }

                foreach (var conv in productConversions)
                {
                    // Inverse direction: from → conv.To → conv.From → to
                    var bridgeIn = SameDimensionFactor(fromUnitId, conv.ToUnitId, units);
                    var bridgeOut = SameDimensionFactor(conv.FromUnitId, toUnitId, units);
                    if (bridgeIn.HasValue && bridgeOut.HasValue)
                        return Result<decimal>.Success(amount * bridgeIn.Value / conv.Factor * bridgeOut.Value);
                }
            }

            return Result<decimal>.Failure(Error.Custom(
                "Catalog.UnresolvableConversion",
                $"No conversion known from unit '{fromUnitId}' to unit '{toUnitId}' for product '{productId}'."));
        };
    }

    /// <summary>Linear scaling factor between two units of the same dimension; null when not resolvable.</summary>
    private static decimal? SameDimensionFactor(
        Guid fromUnitId,
        Guid toUnitId,
        IReadOnlyDictionary<Guid, UnitFact> units)
    {
        if (fromUnitId == toUnitId)
            return 1m;

        if (!units.TryGetValue(fromUnitId, out var fromUnit) ||
            !units.TryGetValue(toUnitId, out var toUnit))
            return null;

        if (fromUnit.Dimension != toUnit.Dimension)
            return null;

        // Both FactorToBase values should be non-null for non-base units; guard against null.
        var fromFactor = fromUnit.FactorToBase ?? 1m;
        var toFactor = toUnit.FactorToBase ?? 1m;
        if (toFactor == 0m) return null;

        return fromFactor / toFactor;
    }
}
