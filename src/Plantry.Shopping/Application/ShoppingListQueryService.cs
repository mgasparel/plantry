using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Application;

/// <summary>
/// Assembles the shopping list read model (SPEC §3a, shopping.md resolved call 3).
/// Loads the aggregate, then enriches each item with catalog data (product name, category name,
/// unit code) resolved via <see cref="IShoppingCatalogReader"/> — a cross-context anti-corruption
/// port implemented by the Web adapter layer. Pantry on-hand quantities and low flags are enriched
/// via <see cref="IShoppingPantryReader"/> (the Shopping→Inventory ACL port) for product-backed items.
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

        var summaries = productIds.Count > 0
            ? await catalog.ResolveSummariesAsync(productIds, ct)
            : (IReadOnlyDictionary<Guid, ShoppingProductSummary>)new Dictionary<Guid, ShoppingProductSummary>();

        var unitCodes = unitIds.Count > 0
            ? await catalog.ResolveUnitCodesAsync(unitIds, ct)
            : (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();

        // Enrich product-backed items with pantry stock levels via the Shopping→Inventory read port.
        // Free-text items have no product id and receive no pantry data.
        var stockLevels = productIds.Count > 0
            ? await pantry.GetStockLevelsAsync(productIds, ct)
            : (IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>)new Dictionary<Guid, ShoppingPantryStockLevel>();

        // Map all items to view models.
        var itemViews = list.Items
            .Select(i => MapItem(i, summaries, unitCodes, stockLevels))
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
        // free-text items (no product_id) fall into Uncategorized (shopping.md resolved call 3).
        var allGroups = uncheckedItems
            .GroupBy(v => v.ProductId.HasValue
                ? (summaries.TryGetValue(v.ProductId.Value, out var s) ? s.CategoryName ?? UncategorizedBucket : UncategorizedBucket)
                : UncategorizedBucket)
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
        IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> stockLevels)
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
            IsLow: isLow);
    }
}
