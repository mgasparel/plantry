using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Read-model query that assembles the Browse page view model (J1/J2, recipes.md resolved call 6).
/// Loads all household recipes (lean — no photo join), then computes <see cref="FulfillmentResult"/>
/// and <see cref="CostPerServing"/> per recipe at default servings via the domain services, then applies
/// the requested filter and sort, returning <see cref="BrowseRecipesResult"/>.
///
/// Sort strategy (per recipes.md resolved call 6):
///   <list type="bullet">
///     <item>Name / Cook-time / Recently-added: local recipe indexes — included in the initial DB query
///       via ordering, but post-filter re-sort is applied to keep the logic simple.</item>
///     <item>Fulfillment / Cost: no index — ordered in the application layer AFTER the cross-context reads.</item>
///   </list>
///
/// All filters are AND-combined (J2).
/// </summary>
public sealed class BrowseRecipesQuery(
    IRecipeRepository recipes,
    ITagRepository tags,
    FulfillmentService fulfillment,
    CostingService costing,
    ITenantContext tenant)
{
    /// <summary>
    /// Executes the browse query. Returns <see cref="BrowseRecipesResult"/> with the full tag list
    /// (for filter chips) and the filtered, sorted recipe rows.
    /// </summary>
    public async Task<BrowseRecipesResult> ExecuteAsync(BrowseRecipesFilter filter, CancellationToken ct = default)
    {
        // All-tags for the filter chip row — must include tags on ANY recipe, even filtered-out ones
        // (including archived tags so existing recipe-tag associations remain filterable).
        var allTags = await tags.ListAllAsync(activeOnly: false, ct);

        if (tenant.HouseholdId is null)
        {
            // Unauthenticated — caller should never reach here (authorization guard on page), but
            // return an empty result rather than throwing.
            return new BrowseRecipesResult(allTags, [], CookableCount: 0);
        }

        // Load all non-archived recipes with their ingredients and tags (no photo per resolved call 3).
        var allRecipes = await recipes.ListForBrowseAsync(ct);
        if (allRecipes.Count == 0)
            return new BrowseRecipesResult(allTags, [], CookableCount: 0);

        // Lightweight existence projection: which recipe ids have a photo stored?
        // Selects only the PK column from recipe_photo — no bytea loaded, honouring resolved call 3.
        var photoIds = await recipes.ListRecipeIdsWithPhotoAsync(ct);

        // Compute fulfillment + cost per recipe sequentially.
        // Task.WhenAll would launch concurrent awaits over the shared scoped DbContexts
        // (CatalogDbContext, InventoryDbContext) within a single HTTP request, which throws
        // InvalidOperationException ("A second operation was started on this context instance
        // before a previous operation completed"). FulfillmentService already documents this
        // constraint at FulfillmentService.cs:51-53 and uses sequential awaits for the same reason.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var computed = new List<RecipeBrowseRow>(allRecipes.Count);
        foreach (var r in allRecipes)
        {
            var fulfillmentResult = await fulfillment.ComputeAsync(r, r.DefaultServings, today, ct);
            var costResult = await costing.ComputeAsync(r, r.DefaultServings, ct);
            computed.Add(BuildRow(r, fulfillmentResult, costResult, photoIds));
        }

        var cookableCount = computed.Count(row => row.FullyCookable);

        // ── Filter ───────────────────────────────────────────────────────────────
        IEnumerable<RecipeBrowseRow> filtered = computed;

        if (!string.IsNullOrEmpty(filter.NameQuery))
        {
            var q = filter.NameQuery.Trim();
            filtered = filtered.Where(row =>
                row.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.TagId.HasValue)
        {
            var tagId = filter.TagId.Value;
            filtered = filtered.Where(row => row.TagIds.Contains(tagId));
        }

        if (filter.UseSoon)
        {
            filtered = filtered.Where(row => row.HasIngredientExpiringSoon);
        }

        // ── Sort ─────────────────────────────────────────────────────────────────
        var rows = filter.Sort switch
        {
            BrowseSort.Name => filter.SortDescending
                ? filtered.OrderByDescending(r => r.Name).ToList()
                : filtered.OrderBy(r => r.Name).ToList(),

            BrowseSort.CookTime => filter.SortDescending
                ? filtered.OrderByDescending(r => r.CookTimeMinutes ?? int.MaxValue).ToList()
                : filtered.OrderBy(r => r.CookTimeMinutes ?? int.MaxValue).ToList(),

            BrowseSort.RecentlyAdded => filter.SortDescending
                ? filtered.OrderByDescending(r => r.CreatedAt).ToList()
                : filtered.OrderBy(r => r.CreatedAt).ToList(),

            BrowseSort.Cost => filter.SortDescending
                ? filtered.OrderByDescending(r => r.CostPerServing ?? decimal.MaxValue).ToList()
                : filtered.OrderBy(r => r.CostPerServing ?? decimal.MaxValue).ToList(),

            // Default: Fulfillment descending (higher pct = more cookable → show first)
            _ => filter.SortDescending
                ? filtered.OrderByDescending(r => r.FulfillmentPct).ToList()
                : filtered.OrderBy(r => r.FulfillmentPct).ToList(),
        };

        return new BrowseRecipesResult(allTags, rows, cookableCount);
    }

    private static RecipeBrowseRow BuildRow(Recipe recipe, FulfillmentResult fulfillmentResult, CostPerServing costResult, IReadOnlySet<RecipeId> photoIds)
    {
        // inStock = ingredients that are InStock or Untracked (satisfied)
        var totalTracked = fulfillmentResult.Lines.Count(l => l.Status != IngredientStatus.Untracked);
        var inStock = fulfillmentResult.Lines.Count(l =>
            l.Status is IngredientStatus.InStock or IngredientStatus.Untracked);

        // Fulfillment percentage (0–100): ratio of InStock+Untracked to all lines.
        // Recipes with no tracked ingredients are trivially 100% cookable.
        int pct;
        if (totalTracked == 0)
            pct = 100;
        else
            pct = (int)Math.Round(
                (double)fulfillmentResult.Lines.Count(l =>
                    l.Status is IngredientStatus.InStock or IngredientStatus.Untracked)
                / fulfillmentResult.Lines.Count * 100);

        var hasExpiringSoon = fulfillmentResult.Lines.Any(l => l.ExpiresWithinDays.HasValue);

        return new RecipeBrowseRow(
            RecipeId: recipe.Id.Value,
            Name: recipe.Name,
            CookTimeMinutes: recipe.CookTimeMinutes,
            DefaultServings: recipe.DefaultServings,
            CreatedAt: recipe.CreatedAt,
            TagIds: recipe.Tags.Select(t => t.TagId.Value).ToList(),
            FullyCookable: fulfillmentResult.Overall.FullyCookable,
            FulfillmentPct: pct,
            InStockCount: inStock,
            TotalIngredientCount: fulfillmentResult.Lines.Count,
            MissingCount: fulfillmentResult.Overall.MissingCount,
            HasIngredientExpiringSoon: hasExpiringSoon,
            CostPerServing: costResult.Completeness != CostCompleteness.None ? costResult.Amount : null,
            CostCompleteness: costResult.Completeness,
            HasPhoto: photoIds.Contains(recipe.Id)
        );
    }
}

// ── Query input ──────────────────────────────────────────────────────────────

/// <summary>
/// Filter + sort parameters for the recipe Browse query (J2).
/// All filter conditions are AND-combined.
/// </summary>
/// <param name="NameQuery">Free-text name search (case-insensitive contains). Empty/null = no filter.</param>
/// <param name="TagId">Tag id to filter by; null = show all tags.</param>
/// <param name="UseSoon">When true, only show recipes where at least one ingredient expires within 4 days.</param>
/// <param name="Sort">Sort dimension.</param>
/// <param name="SortDescending">Sort direction.</param>
public sealed record BrowseRecipesFilter(
    string? NameQuery = null,
    Guid? TagId = null,
    bool UseSoon = false,
    BrowseSort Sort = BrowseSort.Fulfillment,
    bool SortDescending = true);

/// <summary>Sort dimensions for the recipe Browse page (J2, recipes.md resolved call 6).</summary>
public enum BrowseSort
{
    /// <summary>Fulfillment percentage descending (default — shows most-cookable first).</summary>
    Fulfillment,
    /// <summary>Cost per serving ascending (cheapest first by default).</summary>
    Cost,
    /// <summary>Cook time ascending (quickest first by default).</summary>
    CookTime,
    /// <summary>Creation date (most recent first by default).</summary>
    RecentlyAdded,
    /// <summary>Recipe name (A-Z by default).</summary>
    Name,
}

// ── Result ───────────────────────────────────────────────────────────────────

/// <summary>
/// The assembled browse result: all household tags (for filter chips), the filtered and sorted recipe
/// rows, and the count of fully-cookable recipes (for the header subtitle).
/// </summary>
/// <param name="AllTags">All tags in the household, for filter chip rendering. May include tags with no matching recipes.</param>
/// <param name="Rows">Filtered and sorted recipe rows.</param>
/// <param name="CookableCount">Number of fully-cookable recipes (Fulfillment = 100%) across all recipes, not just the filtered set.</param>
public sealed record BrowseRecipesResult(
    IReadOnlyList<Tag> AllTags,
    IReadOnlyList<RecipeBrowseRow> Rows,
    int CookableCount);

/// <summary>
/// Read-model row for one recipe in the browse list. Carries pre-computed fulfillment and cost
/// so the page does not need to re-query the domain services.
/// </summary>
/// <param name="RecipeId">The recipe's id (for linking).</param>
/// <param name="Name">Recipe name.</param>
/// <param name="CookTimeMinutes">Cook time in minutes; null when not set.</param>
/// <param name="DefaultServings">Default serving count.</param>
/// <param name="CreatedAt">When the recipe was created (for Recently-added sort).</param>
/// <param name="TagIds">Tag ids attached to this recipe.</param>
/// <param name="FullyCookable">True when all tracked ingredients are InStock.</param>
/// <param name="FulfillmentPct">0–100 fulfillment percentage (InStock+Untracked / all ingredients).</param>
/// <param name="InStockCount">Count of InStock + Untracked ingredients.</param>
/// <param name="TotalIngredientCount">Total ingredient count.</param>
/// <param name="MissingCount">Count of Missing ingredients.</param>
/// <param name="HasIngredientExpiringSoon">True when any ingredient expires within 4 days.</param>
/// <param name="CostPerServing">Cost per serving; null when <see cref="CostCompleteness"/> is <see cref="CostCompleteness.None"/>.</param>
/// <param name="CostCompleteness">How complete the cost computation is.</param>
/// <param name="HasPhoto">True when the recipe has a photo stored (resolved lazily, not loaded in the browse query).</param>
public sealed record RecipeBrowseRow(
    Guid RecipeId,
    string Name,
    int? CookTimeMinutes,
    int DefaultServings,
    DateTimeOffset CreatedAt,
    IReadOnlyList<Guid> TagIds,
    bool FullyCookable,
    int FulfillmentPct,
    int InStockCount,
    int TotalIngredientCount,
    int MissingCount,
    bool HasIngredientExpiringSoon,
    decimal? CostPerServing,
    CostCompleteness CostCompleteness,
    bool HasPhoto);
