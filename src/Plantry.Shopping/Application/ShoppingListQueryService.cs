using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Application;

/// <summary>
/// Assembles the shopping list read model (SPEC §3a, shopping.md resolved call 3).
/// Loads the aggregate, then enriches each item with catalog data (product name, category name,
/// unit code) resolved via <see cref="IShoppingCatalogReader"/> — a cross-context anti-corruption
/// port implemented by the Web adapter layer. Pantry on-hand quantities and low flags are enriched
/// via <see cref="IShoppingPantryReader"/> (the Shopping→Inventory ACL port) for product-backed items.
/// Recipe names for attribution labels are resolved via <see cref="IShoppingRecipeReader"/>
/// (the Shopping→Recipes ACL port, plantry-26g). Cheapest-active-deal badges are resolved via
/// <see cref="IShoppingDealReader"/> — the Shopping→<b>Pricing</b> ACL port (P5-9, ADR-010: Shopping reads
/// Pricing's read model, never Deals) — evaluated against <see cref="IClock"/> today so a badge appears and
/// lapses with the deal's validity window, and never stored.
///
/// <para>Ordering: unchecked items first (ordered by <c>created_at</c> ascending), checked items
/// last (ordered by <c>checked_at</c> ascending, i.e. most-recently-checked last). This gives a
/// stable, deterministic ordering the UI relies on.</para>
///
/// <para>Grouping: product-backed items bucket by their resolved category name; free-text items
/// (no <c>product_id</c>) fall into the Uncategorized bucket (shopping.md resolved call 3).</para>
/// </summary>
public sealed class ShoppingListQueryService(
    IShoppingListRepository repository,
    IShoppingCatalogReader catalog,
    IShoppingPantryReader pantry,
    IShoppingRecipeReader recipes,
    IShoppingDealReader deals,
    IClock clock,
    ITenantContext tenant)
{
    private const string UncategorizedBucket = "Uncategorized";

    public async Task<ShoppingListView?> GetListAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return null;

        var list = await repository.GetForHouseholdAsync(HouseholdId.From(householdId), ct);
        if (list is null)
            return null;

        // Resolve catalog data for product-backed items in one batch call.
        var productIds = list.Items
            .Where(i => i.ProductId.HasValue)
            .Select(i => i.ProductId!.Value)
            .Distinct()
            .ToList();

        var unitIds = list.Items
            .Where(i => i.UnitId.HasValue)
            .Select(i => i.UnitId!.Value)
            .Distinct()
            .ToList();

        // Collect CategoryIds stored directly on items (used for recategorized free-text items, plantry-259).
        var itemCategoryIds = list.Items
            .Where(i => i.CategoryId.HasValue)
            .Select(i => i.CategoryId!.Value)
            .Distinct()
            .ToList();

        var summaries = productIds.Count > 0
            ? await catalog.ResolveSummariesAsync(productIds, ct)
            : (IReadOnlyDictionary<Guid, ShoppingProductSummary>)new Dictionary<Guid, ShoppingProductSummary>();

        var unitCodes = unitIds.Count > 0
            ? await catalog.ResolveUnitCodesAsync(unitIds, ct)
            : (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();

        // Resolve category name/hue for items with a direct CategoryId (recategorized free-text items).
        // Uses ListCategoriesAsync to batch-resolve; result is keyed by category id.
        IReadOnlyDictionary<Guid, ShoppingCategoryOption> itemCategories =
            new Dictionary<Guid, ShoppingCategoryOption>();
        if (itemCategoryIds.Count > 0)
        {
            var allCategories = await catalog.ListCategoriesAsync(ct);
            itemCategories = allCategories
                .Where(c => itemCategoryIds.Contains(c.CategoryId))
                .ToDictionary(c => c.CategoryId);
        }

        // Enrich product-backed items with pantry stock levels via the Shopping→Inventory read port.
        // Free-text items have no product id and receive no pantry data.
        var stockLevels = productIds.Count > 0
            ? await pantry.GetStockLevelsAsync(productIds, ct)
            : (IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>)new Dictionary<Guid, ShoppingPantryStockLevel>();

        // Resolve recipe names for Recipe-source contributions via the Shopping→Recipes ACL port (plantry-26g).
        // Collect all distinct Recipe SourceRef values across all items' contributions.
        var recipeSourceRefs = list.Items
            .SelectMany(i => i.Contributions)
            .Where(c => c.Source == ItemSource.Recipe && c.SourceRef.HasValue)
            .Select(c => c.SourceRef!.Value)
            .Distinct()
            .ToList();

        var recipeNames = recipeSourceRefs.Count > 0
            ? await recipes.GetRecipeNamesAsync(recipeSourceRefs, ct)
            : (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();

        // Enrich product-backed items with their cheapest active deal via the Shopping→Pricing read port
        // (P5-9). Evaluated against "today" so the badge appears/lapses with the deal's validity window
        // (ADR-010: Shopping reads Pricing's read model, never Deals; the badge is never stored, D11).
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var activeDeals = productIds.Count > 0
            ? await deals.GetActiveDealsAsync(productIds, today, ct)
            : (IReadOnlyDictionary<Guid, ShoppingActiveDeal>)new Dictionary<Guid, ShoppingActiveDeal>();

        // Map all items to view models.
        var itemViews = list.Items
            .Select(i => MapItem(i, summaries, unitCodes, stockLevels, itemCategories, recipeNames, activeDeals))
            .ToList();

        // Partition: checked items sink to the bottom (SPEC §3c). Only unchecked items
        // participate in category grouping so named groups never contain checked items.
        var uncheckedItems = itemViews
            .Where(v => !v.IsChecked)
            .OrderBy(v => v.CreatedAt)
            .ToList();

        var checkedItems = itemViews
            .Where(v => v.IsChecked)
            .OrderBy(v => v.CheckedAt)
            .ToList();

        // Group unchecked items: product-backed items bucket by their resolved category name;
        // free-text items with a CategoryId bucket by the resolved category name (plantry-259);
        // free-text items with no CategoryId fall into Uncategorized (shopping.md resolved call 3).
        var allGroups = uncheckedItems
            .GroupBy(v => v.CategoryName ?? UncategorizedBucket)
            .Select(g => new ShoppingCategoryGroup(g.Key, g.ToList()))
            .OrderBy(g => g.CategoryName == UncategorizedBucket ? 1 : 0)
            .ThenBy(g => g.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Separate Uncategorized (which covers both free-text items and products with no category)
        // from named-category groups.
        var namedGroups = allGroups
            .Where(g => g.CategoryName != UncategorizedBucket)
            .ToList();

        var uncategorized = allGroups
            .Where(g => g.CategoryName == UncategorizedBucket)
            .SelectMany(g => g.Items)
            .ToList();

        return new ShoppingListView(
            ListId: list.Id.Value,
            ListName: list.Name,
            Groups: namedGroups,
            UncategorizedItems: uncategorized,
            CheckedItems: checkedItems,
            TotalCount: itemViews.Count,
            CheckedCount: checkedItems.Count);
    }

    private static ShoppingListItemView MapItem(
        ShoppingListItem item,
        IReadOnlyDictionary<Guid, ShoppingProductSummary> summaries,
        IReadOnlyDictionary<Guid, string> unitCodes,
        IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> stockLevels,
        IReadOnlyDictionary<Guid, ShoppingCategoryOption> itemCategories,
        IReadOnlyDictionary<Guid, string> recipeNames,
        IReadOnlyDictionary<Guid, ShoppingActiveDeal> activeDeals)
    {
        string? productName = null;
        string? categoryName = null;
        int? categoryHue = null;

        if (item.ProductId.HasValue && summaries.TryGetValue(item.ProductId.Value, out var summary))
        {
            productName = summary.Name;
            categoryName = summary.CategoryName;
            categoryHue = summary.CategoryHue;
        }

        // For free-text items (no ProductId), resolve category from the item's own CategoryId
        // if one was set via the recategorize action (plantry-259).
        if (productName is null && item.CategoryId.HasValue
            && itemCategories.TryGetValue(item.CategoryId.Value, out var catOption))
        {
            categoryName = catOption.Name;
            categoryHue = catOption.Hue;
        }

        string? unitCode = item.UnitId.HasValue && unitCodes.TryGetValue(item.UnitId.Value, out var code)
            ? code
            : null;

        // Enrich with pantry stock level if available (product-backed items only).
        decimal? onHand = null;
        string? pantryUnitCode = null;
        bool? isLow = null;
        if (item.ProductId.HasValue && stockLevels.TryGetValue(item.ProductId.Value, out var stock))
        {
            onHand = stock.OnHand;
            pantryUnitCode = stock.UnitCode;
            isLow = stock.IsLow;
        }

        // Build attribution labels from contributions (plantry-26g).
        var attributionLabels = BuildAttributionLabels(item.Contributions, recipeNames);

        // Cheapest-active-deal badge (P5-9): product-backed items only; read from Pricing, never stored.
        string? dealStoreName = null;
        Guid? dealId = null;
        if (item.ProductId.HasValue && activeDeals.TryGetValue(item.ProductId.Value, out var deal))
        {
            dealStoreName = deal.StoreName;
            dealId = deal.DealId;
        }

        return new ShoppingListItemView(
            ItemId: item.Id.Value,
            ListId: item.ShoppingListId.Value,
            ProductId: item.ProductId,
            ProductName: productName,
            FreeText: item.FreeText,
            Quantity: item.Quantity,
            UnitId: item.UnitId,
            UnitCode: unitCode,
            CategoryName: categoryName,
            CategoryHue: categoryHue,
            Note: item.Note,
            IsChecked: item.IsChecked,
            CheckedAt: item.CheckedAt,
            CreatedAt: item.CreatedAt,
            OnHand: onHand,
            PantryUnitCode: pantryUnitCode,
            IsLow: isLow,
            AttributionLabels: attributionLabels,
            DealStoreName: dealStoreName,
            DealId: dealId);
    }

    /// <summary>
    /// Builds the ordered list of typed attribution labels for an item's contributions (plantry-26g).
    /// Each <see cref="AttributionLabel"/> carries a structural <see cref="AttributionKind"/> — set from
    /// the contribution's <see cref="ItemSource"/>, never inferred from the display text — so presentation
    /// (e.g. the recipe icon) keys off the kind, not the wording (plantry-1cfl).
    ///
    /// <para>
    /// Rules (per design session 2026-06-30):
    /// <list type="bullet">
    ///   <item><description>Recipe contributions: group by resolved recipe name (de-duplication by name),
    ///     count distinct SourceRef values per name. Emit <c>(Recipe, "for {Name}")</c> when count=1,
    ///     <c>(Recipe, "for {Name} ×N")</c> when count>1. Ordered by first-seen position for stable rendering.</description></item>
    ///   <item><description>Manual contributions: emit <c>(Manual, "added by you")</c> (always exactly once, regardless
    ///     of how many manual contributions exist — currently at most one per domain model).</description></item>
    ///   <item><description>MealPlan/Deal: omitted (no resolution port yet; additive when future ports land).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>Returns an empty list when no resolvable attribution exists.</para>
    /// </summary>
    private static IReadOnlyList<AttributionLabel> BuildAttributionLabels(
        IReadOnlyList<ShoppingListItemContribution> contributions,
        IReadOnlyDictionary<Guid, string> recipeNames)
    {
        var labels = new List<AttributionLabel>();

        // ── Recipe contributions ──────────────────────────────────────────────
        // Group by resolved recipe name (deduped by name, not by SourceRef).
        // Each distinct (SourceRef, resolvedName) pair represents one contribution.
        // We count the number of distinct SourceRefs that map to the same name.
        var recipeContributions = contributions
            .Where(c => c.Source == ItemSource.Recipe && c.SourceRef.HasValue)
            .ToList();

        if (recipeContributions.Count > 0)
        {
            // Build (name, count of distinct SourceRefs) pairs, preserving first-seen order of names.
            var nameOrder = new List<string>();
            var nameToRefCount = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var c in recipeContributions)
            {
                if (!recipeNames.TryGetValue(c.SourceRef!.Value, out var name))
                    continue; // recipe deleted or not found — skip

                if (!nameToRefCount.ContainsKey(name))
                {
                    nameOrder.Add(name);
                    nameToRefCount[name] = 0;
                }
                nameToRefCount[name]++;
            }

            foreach (var name in nameOrder)
            {
                var count = nameToRefCount[name];
                var text = count > 1 ? $"for {name} ×{count}" : $"for {name}";
                labels.Add(new AttributionLabel(AttributionKind.Recipe, text));
            }
        }

        // ── Manual contributions ──────────────────────────────────────────────
        if (contributions.Any(c => c.Source == ItemSource.Manual))
        {
            labels.Add(new AttributionLabel(AttributionKind.Manual, "added by you"));
        }

        // MealPlan / Deal: no port yet — not emitted; additive when future ports land.

        return labels;
    }
}
