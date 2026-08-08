using Microsoft.Extensions.Logging;
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
/// <para>Every recipe — with or without sub-recipe inclusions — is expanded through
/// <see cref="RecipeExpansionService"/> BEFORE costing/fulfillment (recipe-composition.md §7, D4) —
/// the same choke point Recipe Details and the edit-meal popup already go through — so the meal-plan
/// card cannot silently undercount a recipe's cost by dropping its sub-recipes' ingredients
/// (plantry-yqse). Expansion runs over <see cref="WeekBag"/> facts already loaded by
/// <see cref="MealPlanWeekReadModel"/>'s inclusion-closure query, via the batched in-memory
/// <see cref="RecipeExpansionService.ExpandAsync(RecipeId,IReadOnlyDictionary{RecipeId,Recipe},CancellationToken)"/>
/// overload — it issues zero further round-trips, so this stays within ADR-021 rule 1 despite being
/// Task-shaped. On a dangling/cyclic inclusion (defensive — N4/N5 should prevent this) the dish
/// degrades to flat (direct-ingredients-only) computation rather than failing the whole week render,
/// mirroring <see cref="Plantry.Web.MealPlanning.RecipeReadModelAdapter"/>'s identical fallback.</para>
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
    private readonly RecipeExpansionService _expansionService;
    private readonly IClock _clock;
    private readonly int _expiringSoonDays;
    private readonly ILogger<WeekBagEnricher> _logger;

    // Memo cache: keyed by (recipeId, servings). Populated on first call, reused on subsequent ones.
    private readonly Dictionary<(Guid RecipeId, int Servings), RecipeDishEnrichment?> _memo = [];

    // Lazily built once per enricher instance: every bag recipe (root + transitively-included subs)
    // as a Recipe domain object with BOTH Ingredients and Inclusions replaced from bag facts, so
    // RecipeExpansionService's batched resolver can walk the whole closure with zero further I/O.
    private Dictionary<RecipeId, Recipe>? _recipeDomainCache;

    public WeekBagEnricher(
        WeekBag bag,
        FulfillmentService fulfillmentService,
        CostingService costingService,
        RecipeExpansionService expansionService,
        IClock clock,
        int expiringSoonDays,
        ILogger<WeekBagEnricher> logger)
    {
        _bag = bag;
        _fulfillmentService = fulfillmentService;
        _costingService = costingService;
        _expansionService = expansionService;
        _clock = clock;
        _expiringSoonDays = expiringSoonDays;
        _logger = logger;
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
    public async Task<RecipeDishEnrichment?> EnrichAsync(
        Guid recipeId, int servings, DateOnly today, CancellationToken ct = default)
    {
        var key = (recipeId, servings);
        if (_memo.TryGetValue(key, out var cached))
            return cached;

        var result = await ComputeEnrichmentAsync(recipeId, servings, today, ct);
        _memo[key] = result;
        return result;
    }

    // ── Private compute ───────────────────────────────────────────────────────

    private async Task<RecipeDishEnrichment?> ComputeEnrichmentAsync(
        Guid recipeId, int servings, DateOnly today, CancellationToken ct)
    {
        var recipeFact = _bag.GetRecipe(recipeId);
        if (recipeFact is null)
            return null;

        var directIngredients = _bag.GetIngredients(recipeId);
        var directInclusions = _bag.GetInclusions(recipeId);

        // A recipe with no ingredients AND no inclusions (e.g. just loaded for name display) has
        // nothing to enrich. Still return a result with 100% (untracked-only convention, C12) so the
        // cell renders. A legitimately persisted recipe always has at least one of the two (R3'), so
        // this is the "not really loaded for costing" case, not a real recipe shape.
        if (directIngredients.Count == 0 && directInclusions.Count == 0)
        {
            return new RecipeDishEnrichment(
                FulfillmentPercent: 100,
                TotalCost: null,
                CostIsPartial: false,
                HasExpiringIngredients: false);
        }

        // Resolve the line set to cost/fulfill: expansion runs UNCONDITIONALLY — even for a recipe
        // with no inclusions — because ExpandAsync → AggregateByProductAndUnit also merges duplicate
        // (ProductId, UnitId) lines, and recipes.recipe_ingredient has no product-uniqueness
        // constraint (only UNIQUE (recipe_id, ordinal)), so a recipe may legitimately list the same
        // product twice. Recipe Details and RecipeReadModelAdapter.GetEnrichmentAsync expand+aggregate
        // unconditionally; running the same pipeline here keeps the card's effective line set
        // byte-identical to the adapter's (the invariant this ticket exists to close). Degrades to the
        // flat direct-ingredient view only when expansion fails on a map-miss/dangling/cyclic
        // inclusion (defensive — N4/N5 should prevent this).
        IReadOnlyList<(Guid ProductId, decimal? Quantity, Guid? UnitId)> lines;
        {
            var rid = RecipeId.From(recipeId);
            var recipeDomainMap = GetRecipeDomainMap();

            var expanded = recipeDomainMap.ContainsKey(rid)
                ? await _expansionService.ExpandAsync(rid, recipeDomainMap, ct)
                : Result<IReadOnlyList<ExpandedLine>>.Failure(Error.NotFound);

            if (expanded.IsSuccess)
            {
                lines = expanded.Value.AggregateByProductAndUnit()
                    .Select(l => (l.ProductId, l.Quantity, l.UnitId))
                    .ToList();
            }
            else
            {
                // Degrade to flat rather than fail the whole week render (defensive — N4/N5 should
                // prevent a dangling/cyclic inclusion at save) — but log it: a silent degrade here is a
                // silently WRONG (undercounted) card price, the exact failure mode this ticket exists
                // to fix, with zero operational signal otherwise.
                _logger.LogWarning(
                    "Meal-plan week card degraded to flat costing for recipe {RecipeId}: inclusion expansion failed with {ErrorCode}",
                    recipeId, expanded.Error.Code);
                lines = directIngredients
                    .OrderBy(i => i.Ordinal)
                    .Select(i => (i.ProductId, i.Quantity, i.UnitId))
                    .ToList();
            }
        }

        // Build the Recipe domain object from the resolved lines (no EF round-trip).
        // Recipe.Create + ReplaceIngredients are public APIs; we use SystemClock since the
        // timestamp is irrelevant for a read-only computation. DefaultServings stays the ROOT
        // recipe's — an expanded line's quantity already carries the inclusion factor baked in
        // (RecipeExpansionService), so scaling against the root's DefaultServings here is the exact
        // same "scale = desiredServings / defaultServings" rule CostingService.ComputeExpandedAsync
        // uses (mirrors RecipeReadModelAdapter.GetEnrichmentAsync).
        var recipe = BuildRecipe(recipeFact, lines);

        // Build adapter dictionaries for the pure compute overloads, over the product ids the
        // resolved (flat or expanded) line set actually references — a sub-recipe's ingredients need
        // catalog/stock/price facts too, exactly like the root's own direct ingredients.
        var productIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();

        // Converter is built first so BuildStockById can use it when summing multi-unit lots
        // into the product's default unit (matching InventoryStockReaderAdapter behaviour).
        var converter = BuildConverter();
        var catalogById = BuildCatalogById(productIds);
        var stockById = BuildStockById(catalogById, converter);
        var priceById = BuildPriceById(productIds, catalogById);

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
            // ComputeEnrichmentAsync threads the bag's substitution edges through.
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

    /// <summary>
    /// Builds a transient Recipe domain object from a resolved (flat or expanded) line set (no EF).
    /// <paramref name="lines"/> is either a recipe's direct ingredients or its expanded
    /// <see cref="EffectiveIngredient"/> set (D4) — both shapes reduce to the same
    /// (ProductId, Quantity, UnitId) tuple, so this single builder serves both callers in
    /// <see cref="ComputeEnrichmentAsync"/> and the resulting pseudo-recipe's <c>Ingredients</c> is what
    /// downstream builders (<see cref="BuildCatalogById"/>, <see cref="BuildPriceById"/>) resolve
    /// product ids from regardless of which path produced them.
    /// </summary>
    private static Recipe BuildRecipe(
        RecipeFact recipeFact,
        IReadOnlyList<(Guid ProductId, decimal? Quantity, Guid? UnitId)> lines)
    {
        // Use a sentinel HouseholdId — only the ingredient data matters for pure compute.
        var household = HouseholdId.From(Guid.Empty);
        var recipe = Recipe.Create(household, recipeFact.Name, recipeFact.DefaultServings, SystemClock.Instance).Value;

        var ingredientLines = lines
            .Select((l, idx) => new IngredientLine(
                l.ProductId,
                l.Quantity,
                l.UnitId,
                null,
                idx)) // re-number from 0 to satisfy R6 contiguity
            .ToList();

        recipe.ReplaceIngredients(ingredientLines, SystemClock.Instance);
        return recipe;
    }

    /// <summary>
    /// Lazily builds every bag recipe (root recipes AND every transitively-included sub-recipe the
    /// inclusion-closure query loaded) as a Recipe domain object with both Ingredients and Inclusions
    /// replaced from bag facts — the pre-loaded map <see cref="RecipeExpansionService"/>'s batched
    /// overload resolves against, so expansion issues zero further round-trips (ADR-021 rule 1). Built
    /// once per enricher instance (memoized) since the same subs are frequently shared across several
    /// dishes in the same week.
    /// </summary>
    private IReadOnlyDictionary<RecipeId, Recipe> GetRecipeDomainMap()
    {
        if (_recipeDomainCache is not null) return _recipeDomainCache;

        var map = new Dictionary<RecipeId, Recipe>();
        foreach (var guidId in _bag.Recipes.Keys)
        {
            var built = TryBuildRecipeDomain(guidId);
            if (built is not null)
                map[built.Id] = built;
        }

        _recipeDomainCache = map;
        return map;
    }

    /// <summary>
    /// Builds one bag recipe's full Recipe domain object (ingredients + inclusions) for the expansion
    /// resolver map. Returns null when the recipe cannot be reconstructed — either it has no lines at
    /// all (should not happen for a legitimately persisted recipe, R3') or the reconstructed line set
    /// fails <see cref="RecipeLineSet.Create"/>'s validation (also defensive); either way the caller
    /// simply omits it from the map, which degrades that ONE recipe's expansion to
    /// <c>Recipes.NotFound</c>/<c>Recipes.ExpansionSubNotFound</c> rather than throwing.
    /// </summary>
    private Recipe? TryBuildRecipeDomain(Guid recipeId)
    {
        var fact = _bag.GetRecipe(recipeId);
        if (fact is null) return null;

        var ingredients = _bag.GetIngredients(recipeId);
        var inclusions = _bag.GetInclusions(recipeId);
        if (ingredients.Count == 0 && inclusions.Count == 0)
        {
            _logger.LogWarning(
                "WeekBagEnricher could not build a Recipe domain object for {RecipeId}: no ingredients and no inclusions loaded (should not happen for a legitimately persisted recipe, R3').",
                recipeId);
            return null;
        }

        // Rehydrate (not Create) — the resolver map RecipeExpansionService walks is keyed by the real
        // RecipeId, and its internal cycle-ancestor guard seeds itself from the root recipe's OWN Id, so
        // a recipe built with a random/fresh id here would silently defeat that guard for a defensively-
        // cyclic inclusion chain that loops back through the root specifically (see Recipe.Rehydrate).
        var household = HouseholdId.From(Guid.Empty);
        var createResult = Recipe.Rehydrate(RecipeId.From(recipeId), household, fact.Name, fact.DefaultServings, SystemClock.Instance);
        if (createResult.IsFailure)
        {
            _logger.LogWarning(
                "WeekBagEnricher could not rehydrate recipe {RecipeId}: {ErrorCode}", recipeId, createResult.Error.Code);
            return null;
        }
        var recipe = createResult.Value;

        // Ordinals are re-minted contiguous across the UNION of both line types (N3) — the original
        // bag ordinals only needed to be per-type-contiguous relative to the real recipe; regenerating
        // them here is safe because expansion orders purely by (Path, IngredientId)/DFS structure, not
        // by these synthetic ordinals, and AggregateByProductAndUnit sums regardless of line order.
        var ordinal = 0;
        var ingredientLines = ingredients
            .OrderBy(i => i.Ordinal)
            .Select(i => new IngredientLine(i.ProductId, i.Quantity, i.UnitId, null, ordinal++))
            .ToList();
        var inclusionLines = inclusions
            .OrderBy(i => i.Ordinal)
            .Select(i => new InclusionLine(RecipeId.From(i.SubRecipeId), i.Servings, i.GroupHeading, ordinal++))
            .ToList();

        var lineSetResult = RecipeLineSet.Create(ingredientLines, inclusionLines, recipe.Id);
        if (lineSetResult.IsFailure)
        {
            _logger.LogWarning(
                "WeekBagEnricher could not build a valid line set for recipe {RecipeId}: {ErrorCode}",
                recipeId, lineSetResult.Error.Code);
            return null;
        }

        recipe.ReplaceLines(lineSetResult.Value, SystemClock.Instance);
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
        IReadOnlyList<Guid> productIds)
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

        foreach (var productId in productIds)
        {
            AddWithVariants(productId);

            if (_bag.SubstitutionsByTargetProduct.TryGetValue(productId, out var edges))
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
        IReadOnlyList<Guid> productIds,
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

        foreach (var productId in productIds)
        {
            AddPriceIfPresent(productId);

            if (catalogById.TryGetValue(productId, out var catalogProduct))
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
    private Func<Guid, decimal, Guid, Guid, Result<decimal>> BuildConverter()
    {
        // Map once per call — the unit set is shared across every product this recipe touches.
        // UnitFact.FactorToBase is decimal? (nullable in the flat SQL projection, MealPlanWeekReadModel.cs);
        // a null value maps to 1m, preserving today's week-bag behaviour exactly (deliberate, not a
        // projection change — plantry-jvd7 AC5). DimensionExtensions.Parse is the same helper
        // PantryDbContext's Dimension value conversion uses.
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
