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
/// (the Shopping→Recipes ACL port, plantry-26g). MealPlan-source attribution labels ("for Mon dinner")
/// are resolved via <see cref="IShoppingMealPlanReader"/> (the Shopping→Meal Planning ACL port) and
/// Deal-source attribution labels ("on sale at {store}") via <see cref="IShoppingDealAttributionReader"/>
/// (the Shopping→Deals ACL port) — both added in plantry-jwyb. Cheapest-active-deal badges are resolved via
/// <see cref="IShoppingDealReader"/> — the Shopping→<b>Pricing</b> ACL port (P5-9, ADR-010: Shopping reads
/// Pricing's read model for the badge) — evaluated against <see cref="IClock"/> today so a badge appears and
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
    IShoppingMealPlanReader mealPlans,
    IShoppingDealAttributionReader dealAttribution,
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

        // Resolve MealPlan slot labels (day + meal type) for MealPlan-source contributions via the
        // Shopping→Meal Planning ACL port (plantry-jwyb). SourceRef is a meal-plan slot/entry id.
        var mealPlanSourceRefs = list.Items
            .SelectMany(i => i.Contributions)
            .Where(c => c.Source == ItemSource.MealPlan && c.SourceRef.HasValue)
            .Select(c => c.SourceRef!.Value)
            .Distinct()
            .ToList();

        var mealPlanSlots = mealPlanSourceRefs.Count > 0
            ? await mealPlans.GetMealPlanSlotsAsync(mealPlanSourceRefs, ct)
            : (IReadOnlyDictionary<Guid, ShoppingMealPlanSlot>)new Dictionary<Guid, ShoppingMealPlanSlot>();

        // Resolve store names for Deal-source contributions via the Shopping→Deals ACL port (plantry-jwyb).
        // SourceRef is the deal_id that placed the item on the list — distinct from the product-keyed
        // cheapest-active-deal badge below.
        var dealSourceRefs = list.Items
            .SelectMany(i => i.Contributions)
            .Where(c => c.Source == ItemSource.Deal && c.SourceRef.HasValue)
            .Select(c => c.SourceRef!.Value)
            .Distinct()
            .ToList();

        var dealStoreNames = dealSourceRefs.Count > 0
            ? await dealAttribution.GetDealStoreNamesAsync(dealSourceRefs, ct)
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
            .Select(i => MapItem(i, summaries, unitCodes, stockLevels, itemCategories, recipeNames,
                mealPlanSlots, dealStoreNames, activeDeals))
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

    /// <summary>
    /// Reads the current household's shopping list and projects, for <paramref name="recipeId"/>, the
    /// state the recipe Detail page needs to render the "Add missing" / "Add all" buttons in their true
    /// per-delta state (plantry-gsj, refining the coarse yt0m boolean):
    /// <list type="bullet">
    ///   <item><description><b>ContributedByProduct</b> — this recipe's own contributed quantity on each
    ///     UNCHECKED product row (Source=Recipe, SourceRef=recipeId). Used to decide, per shortfall line,
    ///     whether the recipe already covers it (→ "Added"), partially covers it (→ "Add N more"), or has
    ///     not contributed it yet (→ "Add N missing").</description></item>
    ///   <item><description><b>CheckedOffProducts</b> — products with a checked-off row (any source). A
    ///     shortfall line for such a product is treated as covered (the sync will not resurrect a completed
    ///     intent).</description></item>
    /// </list>
    /// Tenant-scoped via the repository's household filter + RLS backstop; returns
    /// <see cref="RecipeContributionState.Empty"/> when there is no household context or no list yet.
    /// </summary>
    public async Task<RecipeContributionState> GetRecipeContributionStateAsync(Guid recipeId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return RecipeContributionState.Empty;

        var list = await repository.GetForHouseholdAsync(HouseholdId.From(householdId), ct);
        if (list is null)
            return RecipeContributionState.Empty;

        var contributed = new Dictionary<Guid, decimal>();
        var checkedProducts = new HashSet<Guid>();

        foreach (var item in list.Items)
        {
            if (!item.ProductId.HasValue)
                continue;

            if (item.IsChecked)
            {
                checkedProducts.Add(item.ProductId.Value);
                continue;
            }

            var contrib = item.Contributions.FirstOrDefault(
                c => c.Source == ItemSource.Recipe && c.SourceRef == recipeId);
            if (contrib is not null)
                contributed[item.ProductId.Value] = contrib.Quantity ?? 0m;
        }

        return new RecipeContributionState(contributed, checkedProducts);
    }

    private static ShoppingListItemView MapItem(
        ShoppingListItem item,
        IReadOnlyDictionary<Guid, ShoppingProductSummary> summaries,
        IReadOnlyDictionary<Guid, string> unitCodes,
        IReadOnlyDictionary<Guid, ShoppingPantryStockLevel> stockLevels,
        IReadOnlyDictionary<Guid, ShoppingCategoryOption> itemCategories,
        IReadOnlyDictionary<Guid, string> recipeNames,
        IReadOnlyDictionary<Guid, ShoppingMealPlanSlot> mealPlanSlots,
        IReadOnlyDictionary<Guid, string> dealStoreNames,
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

        // Build attribution labels from contributions (plantry-26g, plantry-jwyb).
        var attributionLabels = BuildAttributionLabels(item.Contributions, recipeNames, mealPlanSlots, dealStoreNames);

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
    /// Builds the ordered list of typed attribution labels for an item's contributions (plantry-26g,
    /// plantry-jwyb). Each <see cref="AttributionLabel"/> carries a structural <see cref="AttributionKind"/>
    /// — set from the contribution's <see cref="ItemSource"/>, never inferred from the display text — so
    /// presentation (e.g. the recipe icon) keys off the kind, not the wording (plantry-1cfl).
    ///
    /// <para>
    /// Emission order is fixed <b>Recipe → MealPlan → Deal → Manual</b> so "added by you" stays last as the
    /// catch-all. Rules:
    /// <list type="bullet">
    ///   <item><description>Recipe contributions: group by resolved recipe name (de-duplication by name),
    ///     count distinct SourceRef values per name. Emit <c>(Recipe, "for {Name}")</c> when count=1,
    ///     <c>(Recipe, "for {Name} ×N")</c> when count>1. First-seen order.</description></item>
    ///   <item><description>MealPlan contributions: one line <b>per distinct meal-plan slot</b> — no ×N roll-up
    ///     (each slot is its own line). Resolved slot → <c>(MealPlan, "for {Day} {meal}")</c> where Day is the
    ///     3-letter weekday abbreviation and meal is the lowercased slot label (e.g. "for Mon dinner").
    ///     Unresolved slots fall back to <c>(MealPlan, "for your meal plan")</c>. De-duplicated by the final
    ///     display text, first-seen order (so all unresolved refs collapse to a single fallback line).</description></item>
    ///   <item><description>Deal contributions: resolved store → <c>(Deal, "on sale at {Store}")</c>; unresolved
    ///     store falls back to <c>(Deal, "on sale")</c>. De-duplicated by display text, first-seen order.</description></item>
    ///   <item><description>Manual contributions: emit <c>(Manual, "added by you")</c> (always exactly once, regardless
    ///     of how many manual contributions exist — currently at most one per domain model).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>Returns an empty list when no resolvable attribution exists.</para>
    /// </summary>
    private static IReadOnlyList<AttributionLabel> BuildAttributionLabels(
        IReadOnlyList<ShoppingListItemContribution> contributions,
        IReadOnlyDictionary<Guid, string> recipeNames,
        IReadOnlyDictionary<Guid, ShoppingMealPlanSlot> mealPlanSlots,
        IReadOnlyDictionary<Guid, string> dealStoreNames)
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

        // ── MealPlan contributions ────────────────────────────────────────────
        // One line per distinct meal-plan slot (no ×N roll-up). Resolved slots render "for {Day} {meal}";
        // unresolved slots collapse to a single "for your meal plan" fallback. De-dup by final text.
        AddDistinctLabels(
            labels,
            contributions.Where(c => c.Source == ItemSource.MealPlan),
            AttributionKind.MealPlan,
            c => c.SourceRef.HasValue && mealPlanSlots.TryGetValue(c.SourceRef.Value, out var slot)
                ? $"for {AbbreviateDay(slot.Day)} {slot.MealType.ToLowerInvariant()}"
                : "for your meal plan");

        // ── Deal contributions ────────────────────────────────────────────────
        // Resolved store → "on sale at {Store}"; unresolved → plain "on sale". De-dup by final text.
        AddDistinctLabels(
            labels,
            contributions.Where(c => c.Source == ItemSource.Deal),
            AttributionKind.Deal,
            c => c.SourceRef.HasValue && dealStoreNames.TryGetValue(c.SourceRef.Value, out var store)
                ? $"on sale at {store}"
                : "on sale");

        // ── Manual contributions ──────────────────────────────────────────────
        if (contributions.Any(c => c.Source == ItemSource.Manual))
        {
            labels.Add(new AttributionLabel(AttributionKind.Manual, "added by you"));
        }

        return labels;
    }

    /// <summary>
    /// Appends one <see cref="AttributionLabel"/> of <paramref name="kind"/> per distinct display text
    /// produced by <paramref name="textFor"/> over <paramref name="source"/>, in first-seen order. Used by
    /// the MealPlan and Deal branches: each distinct resolved slot/store is its own line (no ×N roll-up),
    /// and repeated or unresolved refs that map to the same text collapse to a single line (plantry-jwyb).
    /// </summary>
    private static void AddDistinctLabels(
        List<AttributionLabel> labels,
        IEnumerable<ShoppingListItemContribution> source,
        AttributionKind kind,
        Func<ShoppingListItemContribution, string> textFor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in source)
        {
            var text = textFor(c);
            if (seen.Add(text))
                labels.Add(new AttributionLabel(kind, text));
        }
    }

    /// <summary>Three-letter, culture-invariant weekday abbreviation (Mon, Tue, …) for a label.</summary>
    private static string AbbreviateDay(DayOfWeek day) =>
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)day];
}
