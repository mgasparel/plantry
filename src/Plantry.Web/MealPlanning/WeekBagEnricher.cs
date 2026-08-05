using Plantry.Pantry.Domain;
using Plantry.Planning.Application;
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
    /// Returns true when the recipe has a stored photo (plantry-tyvg), or false when the recipe is
    /// not loaded in the bag (e.g. archived between load and render) — the caller falls back to the
    /// gradient placeholder in that case, same as a missing name.
    /// </summary>
    public bool GetRecipeHasPhoto(Guid recipeId) =>
        _bag.GetRecipe(recipeId)?.HasPhoto ?? false;

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
        var priceById = BuildPriceById(ingredients, catalogById);

        // Fulfillment (pure — zero round-trips). The week bag's substitution edges (plantry-aqpa.2,
        // LoadAsync's Query 2b) are threaded through exactly like catalogById/stockById, so a recipe
        // whose direct stock alone is short but a household substitute covers it reads
        // InStockViaSubstitute here too — the same answer Recipe Details/Browse would give.
        var fulfillment = _fulfillmentService.Compute(
            recipe, servings, today, catalogById, stockById, _bag.SubstitutionsByTargetProduct, converter,
            UnitFactorFrom(converter), _expiringSoonDays);

        // Cost (pure — zero round-trips).
        var cost = _costingService.Compute(recipe, servings, catalogById, priceById, converter);

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
            // InStockViaSubstitute (plantry-aqpa.2) counts as fully satisfied — reachable now that
            // ComputeEnrichment threads the bag's substitution edges through.
            var inStockCount = trackedLines.Count(l =>
                l.Status is IngredientStatus.InStock or IngredientStatus.InStockViaSubstitute);
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
    /// Includes the product itself and its variant children when it is a parent (DM-19), plus —
    /// for any ingredient with one-hop substitution edges (plantry-aqpa.2) — each substitute product
    /// and ITS variant children too, mirroring FulfillmentService.ResolveCatalogAndStockAsync's own
    /// catalog union (src/Plantry.Recipes/Domain/FulfillmentService.cs). The bag's LoadAsync already
    /// folded every substitute product id into its own product/stock/conversion queries, so
    /// <see cref="WeekBag.GetProduct"/> resolves them without a further round-trip here.
    /// </summary>
    private IReadOnlyDictionary<Guid, CatalogProduct> BuildCatalogById(
        IReadOnlyList<IngredientFact> ingredients)
    {
        var result = new Dictionary<Guid, CatalogProduct>();

        void AddWithVariants(Guid productId)
        {
            var fact = _bag.GetProduct(productId);
            if (fact is null) return;

            AddProductIfAbsent(result, fact);

            // Include variant children so FulfillmentService can roll up DM-19 parent stock — applies
            // equally to a direct ingredient's parent product and to a substitute that is itself a
            // parent (factor 1.0, not a second substitution hop).
            foreach (var variantId in fact.VariantProductIds)
            {
                var variantFact = _bag.GetProduct(variantId);
                if (variantFact is not null)
                    AddProductIfAbsent(result, variantFact);
            }
        }

        foreach (var ing in ingredients)
        {
            AddWithVariants(ing.ProductId);

            if (_bag.SubstitutionsByTargetProduct.TryGetValue(ing.ProductId, out var edges))
                foreach (var edge in edges)
                    AddWithVariants(edge.SubstituteProductId);
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
    /// <see cref="Plantry.Recipes.Application.ProductStock.AvailableQuantity"/> is the sum of ALL active lots converted
    /// into the product's default unit, with lots that fail conversion contributing 0.
    /// This ensures parity when a product is stocked in multiple units (e.g. 2 kg + 500 g).
    /// </summary>
    private IReadOnlyDictionary<Guid, Plantry.Recipes.Application.ProductStock> BuildStockById(
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById,
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter)
    {
        var result = new Dictionary<Guid, Plantry.Recipes.Application.ProductStock>();

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

            result[productId] = new Plantry.Recipes.Application.ProductStock(
                productId,
                totalAvailable,
                defaultUnitId,
                stockFact.SoonestExpiry);
        }

        return result;
    }

    /// <summary>
    /// Builds the PricePoint lookup for the pure CostingService.Compute overload. Includes the
    /// product itself and, for a parent product (DM-19), its variant children's prices too — a parent
    /// is never itself priced, so <c>CostingService</c>'s cheapest-variant rollup needs the variant
    /// prices present in this dictionary (mirrors <see cref="BuildCatalogById"/>'s variant walk).
    /// </summary>
    private IReadOnlyDictionary<Guid, PricePoint> BuildPriceById(
        IReadOnlyList<IngredientFact> ingredients,
        IReadOnlyDictionary<Guid, CatalogProduct> catalogById)
    {
        var result = new Dictionary<Guid, PricePoint>();

        void AddPriceIfPresent(Guid productId)
        {
            if (result.ContainsKey(productId)) return;

            var priceFact = _bag.GetLatestPrice(productId);
            if (priceFact is null) return;

            result[productId] = new PricePoint(
                priceFact.ProductId,
                priceFact.Price,
                priceFact.Quantity,
                priceFact.UnitId,
                priceFact.UnitPrice);
        }

        foreach (var ing in ingredients)
        {
            AddPriceIfPresent(ing.ProductId);

            if (catalogById.TryGetValue(ing.ProductId, out var catalogProduct))
            {
                foreach (var variantId in catalogProduct.VariantProductIds)
                    AddPriceIfPresent(variantId);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the sync converter delegate for the pure compute overloads from the WeekBag units and
    /// product-specific conversions. Delegates entirely to the canonical
    /// <see cref="UnitConverter"/>'s shape-typed <c>Convert</c> overload (plantry-jvd7) — this
    /// method's only job is mapping WeekBag facts to
    /// <see cref="UnitConverter.UnitShape"/>/<see cref="UnitConverter.ConversionShape"/>, entirely in
    /// memory over data the caller already loaded (ADR-021 rule 1: SQL fetches data, C# keeps the
    /// math — no new round-trips here). This is what keeps the week-bag costing/fulfillment path and
    /// Inventory.Consume on one shared conversion algorithm instead of a second, independently
    /// drifting copy.
    /// </summary>
    private Func<Guid, decimal, Guid, Guid, Result<decimal>> BuildConverter(Guid recipeId)
    {
        // Map once per call — the unit set is shared across every product this recipe touches.
        // UnitFact.FactorToBase is decimal? (nullable in the flat SQL projection, MealPlanWeekReadModel.cs);
        // a null value maps to 1m, preserving today's week-bag behaviour exactly (deliberate, not a
        // projection change — plantry-jvd7 AC5). DimensionExtensions.Parse is the same helper
        // CatalogDbContext's Dimension value conversion uses.
        var unitShapes = _bag.Units.Values
            .Select(u => new UnitConverter.UnitShape(u.UnitId, DimensionExtensions.Parse(u.Dimension), u.FactorToBase ?? 1m))
            .ToList();

        return (productId, amount, fromUnitId, toUnitId) =>
        {
            var conversionShapes = _bag.GetConversions(productId)
                .Select(c => new UnitConverter.ConversionShape(c.FromUnitId, c.ToUnitId, c.Factor))
                .ToList();

            return UnitConverter.Convert(amount, fromUnitId, toUnitId, unitShapes, conversionShapes);
        };
    }

    /// <summary>
    /// Derives the amount-independent unit-factor delegate <see cref="FulfillmentService.Compute"/>
    /// requires from the exact-amount <paramref name="converter"/> — probes with amount 1, which is
    /// exact because <see cref="UnitConverter.Convert(decimal,Guid,Guid,System.Collections.Generic.IReadOnlyCollection{UnitConverter.UnitShape},System.Collections.Generic.IReadOnlyCollection{UnitConverter.ConversionShape})"/>
    /// is provably linear/multiplicative (plantry-aqpa.2) — used for a substitution edge's
    /// target-unit landing hop.
    /// </summary>
    private static Func<Guid, Guid, Guid, Result<decimal>> UnitFactorFrom(
        Func<Guid, decimal, Guid, Guid, Result<decimal>> converter) =>
        (productId, fromUnitId, toUnitId) => converter(productId, 1m, fromUnitId, toUnitId);
}
