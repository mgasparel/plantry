using Microsoft.Extensions.Logging;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Planning.Application;

/// <summary>
/// Application service for "Shop for this week" (J6, domain-model §7).
/// Across all PlannedDishes in the week:
/// - Recipe dishes → their Missing/Low ingredients at planned servings (via IRecipeReadModel).
/// - Product dishes → the product itself if short on stock (via IMealPlanStockReader).
/// Note-meals are skipped (no dishes by construction).
///
/// <para>
/// <b>Per-slot provenance (plantry-jie7):</b> missing items are accumulated and written
/// <em>per planned_meal slot</em>, and each slot's write stamps <c>sourceRef = meal.Id</c> (the
/// <c>planned_meal</c> slot id — NOT the whole-plan id). Shopping's per-source contribution model
/// (plantry-9scq) keys contributions by (Source, SourceRef), so the same product needed by two slots
/// yields ONE product line carrying two contributions that SUM (no line fan-out, total unchanged),
/// and the slot ids resolve through <c>IMealPlanRepository.FindSlotLabelsAsync</c> to the
/// "for {Day} {meal}" board labels (plantry-jwyb). A product needed by multiple slots contributes in
/// a single canonical unit — the first unit seen for that product across the week — so the per-slot
/// contributions stay mergeable.
/// </para>
///
/// <para>
/// <b>Intra-context write (ADR-024, plantry-g3da.5):</b> formerly wrote through the MealPlanning→Shopping
/// ACL port <c>IMealPlanShoppingWriter</c> (implemented by <c>MealPlanShoppingWriterAdapter</c> wrapping
/// Shopping's <see cref="AddItemCommand"/>). Now that both halves live in Plantry.Planning, each slot's
/// missing items are added by calling <see cref="AddItemCommand"/> directly — collapsed the same way the
/// Market merge folded <c>RecordDealObservationAdapter</c> into <c>ConfirmDeal</c> calling
/// <see cref="RecordObservationCommand"/> (via <c>Plantry.Market</c>) in-process.
/// </para>
/// </summary>
public sealed class ShopForWeekService(
    IMealPlanRepository mealPlanRepo,
    IRecipeReadModel recipeReader,
    IMealPlanStockReader stockReader,
    IShoppingListRepository shoppingListRepo,
    IShoppingCatalogReader shoppingCatalogReader,
    IClock clock,
    ITenantContext tenant,
    ILogger<ShopForWeekService> logger,
    IMealPlanUnitConverter? unitConverter = null)
{
    /// <summary>
    /// Collects all missing items for the week and adds them to the shopping list.
    /// Returns the number of distinct product lines added (may be 0 when everything is in stock).
    /// </summary>
    public async Task<ShopForWeekResult> ExecuteAsync(
        HouseholdId householdId,
        DateOnly weekStart,
        CancellationToken ct = default)
    {
        var plan = await mealPlanRepo.FindByWeekAsync(householdId, weekStart, ct);
        if (plan is null || plan.PlannedMeals.Count == 0)
            return new ShopForWeekResult(0);

        // Week-level canonical unit per product: the first unit seen for a product anywhere in the
        // week. Every subsequent requirement is converted to that denomination before it is added.
        var unitByProduct = new Dictionary<Guid, Guid>();

        // Distinct product lines added across the whole week — the user-facing "N items" count.
        // Contributions may be many (one per slot per product) but a product is one line.
        var productLines = new HashSet<Guid>();

        var pendingByMeal = new List<(Guid SourceRef, List<MealPlanShoppingItem> Items)>();

        foreach (var meal in plan.PlannedMeals)
        {
            // Note-meals have no dishes — skip (M13 / domain-model §3.2).
            if (meal.Note is not null || meal.PlannedDishes.Count == 0)
                continue;

            // Per-slot accumulation in canonical units.
            var slotMissing = new Dictionary<Guid, decimal>();

            foreach (var dish in meal.PlannedDishes)
            {
                if (dish.RecipeId.HasValue)
                {
                    // Recipe dish: collect missing/low ingredients from Recipes' read model.
                    var ingredients = await recipeReader.GetMissingIngredientsAsync(
                        dish.RecipeId.Value, dish.Servings ?? 0, ct);

                    foreach (var ing in ingredients)
                    {
                        var canonicalQuantity = await ToCanonicalAsync(ing.ProductId, ing.Quantity, ing.UnitId, ct);
                        slotMissing[ing.ProductId] = slotMissing.GetValueOrDefault(ing.ProductId) + canonicalQuantity;
                    }
                }
                else if (dish.ProductId.HasValue)
                {
                    // Product dish: add the product itself if short on stock, expressed in the saved
                    // quantity/unit snapshot rather than today's catalog default.
                    var stock = await stockReader.FindStockAsync(dish.ProductId.Value, ct);

                    if (dish.Quantity is not > 0m || dish.UnitId is not { } plannedUnit || plannedUnit == Guid.Empty)
                        continue;
                    var required = dish.Quantity.Value;
                    var available = stock?.AvailableQuantity ?? 0m;
                    if (stock is not null && stock.DefaultUnitId != plannedUnit)
                    {
                        var converted = unitConverter is null
                            ? Result<decimal>.Failure(Error.Custom("MealPlanning.ConversionUnavailable", "No unit converter is configured."))
                            : await unitConverter.ConvertAsync(dish.ProductId.Value, available, stock.DefaultUnitId, plannedUnit, ct);
                        available = converted.IsSuccess ? converted.Value : 0m;
                    }

                    if (available < required && plannedUnit != Guid.Empty)
                    {
                        var needed = required - available;
                        var canonicalNeeded = await ToCanonicalAsync(dish.ProductId.Value, needed, plannedUnit, ct);
                        slotMissing[dish.ProductId.Value] =
                            slotMissing.GetValueOrDefault(dish.ProductId.Value) + canonicalNeeded;
                    }
                }
            }

            if (slotMissing.Count == 0)
                continue;

            var slotItems = slotMissing
                .Select(kvp => new MealPlanShoppingItem(kvp.Key, kvp.Value, unitByProduct[kvp.Key]))
                .ToList();

            // Stamp the planned_meal SLOT id (NOT plan.Id) so the shopping board resolves the
            // per-slot "for {Day} {meal}" label (plantry-jwyb) and slot contributions sum (plantry-9scq).
            pendingByMeal.Add((meal.Id.Value, slotItems));

            foreach (var productId in slotMissing.Keys)
                productLines.Add(productId);
        }

        if (productLines.Count == 0)
            return new ShopForWeekResult(0);

        foreach (var (sourceRef, items) in pendingByMeal)
            await AddItemsAsync(items, sourceRef, ct);

        logger.LogInformation(
            "ShopForWeek added {ItemCount} missing product line(s) to shopping list for week {WeekStart}.",
            productLines.Count, weekStart);

        return new ShopForWeekResult(productLines.Count);

        async Task<decimal> ToCanonicalAsync(Guid productId, decimal amount, Guid unitId, CancellationToken token)
        {
            if (!unitByProduct.TryGetValue(productId, out var canonical))
            {
                if (unitId == Guid.Empty)
                {
                    logger.LogWarning(
                        "ShopForWeek cannot resolve a planning unit for product {ProductId}; aborting before writes.",
                        productId);
                    throw new InvalidOperationException($"Product {productId} has no usable planning unit.");
                }
                unitByProduct[productId] = canonical = unitId;
            }

            if (canonical == unitId) return amount;
            if (unitConverter is null)
            {
                logger.LogWarning(
                    "ShopForWeek requires a unit conversion for product {ProductId} ({FromUnitId} -> {ToUnitId}) but no converter is configured; aborting before writes.",
                    productId, unitId, canonical);
                throw new InvalidOperationException($"A conversion is required to combine product {productId} quantities in {unitId} and {canonical}.");
            }
            var converted = await unitConverter.ConvertAsync(productId, amount, unitId, canonical, token);
            if (converted.IsFailure)
            {
                logger.LogWarning(
                    "ShopForWeek could not convert product {ProductId} quantities ({FromUnitId} -> {ToUnitId}); aborting before writes.",
                    productId, unitId, canonical);
                throw new InvalidOperationException($"A conversion is required to combine product {productId} quantities in {unitId} and {canonical}.");
            }
            return converted.Value;
        }
    }

    /// <summary>
    /// Adds one slot's missing items to the household's shopping list via Shopping's
    /// <see cref="AddItemCommand"/> directly (formerly the MealPlanning→Shopping ACL adapter
    /// <c>MealPlanShoppingWriterAdapter</c>, collapsed to an intra-context call — ADR-024,
    /// plantry-g3da.5). <paramref name="sourceRef"/> is the <c>planned_meal</c> slot id.
    /// </summary>
    private async Task AddItemsAsync(IEnumerable<MealPlanShoppingItem> items, Guid sourceRef, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var command = new AddItemCommand(
                productId: item.ProductId,
                freeText: null,
                quantity: item.Quantity,
                unitId: item.UnitId,
                note: null,
                source: ItemSource.MealPlan,
                sourceRef: sourceRef,
                intentionalDuplicate: false,
                repository: shoppingListRepo,
                catalogReader: shoppingCatalogReader,
                clock: clock,
                tenant: tenant);

            var result = await command.ExecuteAsync(ct);
            if (result.IsFailure)
            {
                logger.LogError(
                    "ShopForWeek failed to add product {ProductId} to the shopping list for slot {SourceRef}: {ErrorCode} — {ErrorDescription}",
                    item.ProductId, sourceRef, result.Error.Code, result.Error.Description);
                throw new InvalidOperationException(
                    $"ShopForWeek.AddItemsAsync failed for product {item.ProductId}: " +
                    $"{result.Error.Code} — {result.Error.Description}");
            }
        }
    }
}

/// <summary>Result of a ShopForWeek execution.</summary>
/// <param name="ItemsAdded">Number of distinct product lines added to the shopping list (0 = fully stocked).</param>
public sealed record ShopForWeekResult(int ItemsAdded);

/// <summary>
/// One product-backed item in a bulk add-to-shopping-list call (DM-18), consumed by
/// <see cref="ShopForWeekService"/>'s intra-context write to Shopping's <see cref="AddItemCommand"/>.
/// Mirrors <c>Plantry.Recipes.Application.ShoppingItem</c> — a separate copy per context (DM-3).
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">Required quantity, scaled to the desired serving count.</param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3).</param>
public sealed record MealPlanShoppingItem(Guid ProductId, decimal Quantity, Guid UnitId);
