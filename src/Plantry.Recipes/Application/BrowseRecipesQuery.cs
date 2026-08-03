using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Application;

/// <summary>
/// Read-model query that assembles the Browse page view model (J1/J2, recipes.md resolved call 6).
/// Loads all household recipes (lean — no photo join), expands each against the loaded set, then computes
/// <see cref="ExpandedFulfillmentResult"/> and <see cref="CostPerServing"/> per recipe at default servings
/// via the domain services, then applies the requested filter and sort, returning <see cref="BrowseRecipesResult"/>.
///
/// <para>Fulfillment and cost badges reflect a recipe's <b>expanded</b> view (recipe-composition.md §7, D4),
/// so a recipe that draws its ingredients from included sub-recipes shows the same expanded figures on Browse
/// as on its Details page (J5) — no J5/J6 drift. Because Browse sorts by fulfillment/cost, expanded values
/// must be computed EAGERLY for every recipe, so a lazy per-row strategy cannot help. Expansion is done as
/// pure in-memory work against an id→<see cref="Recipe"/> map built from the SAME bulk load Browse already
/// issues (Option B): <see cref="IRecipeRepository.ListForBrowseAsync"/> loads every non-archived recipe with
/// its ingredients + inclusions, so every legitimately-included sub-recipe (N5 blocks archiving an included
/// recipe) is already in the set — zero per-sub round-trips.</para>
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
    IRecipeRatingRepository ratings,
    RecipeExpansionService expansion,
    FulfillmentService fulfillment,
    CostingService costing,
    ITenantContext tenant,
    IClock clock)
{
    /// <summary>
    /// Executes the browse query. Returns <see cref="BrowseRecipesResult"/> with the full tag list
    /// (for filter chips) and the filtered, sorted recipe rows.
    /// </summary>
    /// <param name="filter">Filter/sort parameters.</param>
    /// <param name="userId">
    /// The signed-in member's id (plantry-zlwp.1), used to populate each row's <see cref="RecipeBrowseRow.MyStars"/>.
    /// Null yields <c>MyStars = null</c> on every row (household average/count are still computed) — an
    /// unauthenticated caller or one that has not yet threaded the acting user through degrades gracefully.
    /// </param>
    public async Task<BrowseRecipesResult> ExecuteAsync(
        BrowseRecipesFilter filter, Guid? userId = null, CancellationToken ct = default)
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

        // Batched rating lookup (plantry-zlwp.1): ONE round-trip for every household rating across the
        // whole browse set, then grouped by recipe below — mirrors photoIds' batch-then-thread-through
        // shape so MyStars/HouseholdAvg/RatedCount never cost a per-recipe query in the BuildRow loop.
        var ratingsByRecipe = (await ratings.ListByRecipeIdsAsync(allRecipes.Select(r => r.Id).ToList(), ct))
            .GroupBy(r => r.RecipeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RecipeRating>)g.ToList());

        // Batched in-memory expansion (Option B, recipe-composition.md D4/D5): every non-archived recipe is
        // already loaded (with ingredients + inclusions) by ListForBrowseAsync, so build an id→Recipe map and
        // expand each recipe against the MAP — no per-sub round-trips. Expansion runs through the single
        // RecipeExpansionService choke point (map-resolver overload), so Browse's expanded figures cannot
        // drift from the Details page's repo-path figures on factor math / rounding / duplicate subs.
        var recipesById = allRecipes.ToDictionary(r => r.Id);

        // Compute fulfillment + cost per recipe sequentially.
        // Task.WhenAll would launch concurrent awaits over the shared scoped DbContexts
        // (CatalogDbContext, InventoryDbContext) within a single HTTP request, which throws
        // InvalidOperationException ("A second operation was started on this context instance
        // before a previous operation completed"). FulfillmentService already documents this
        // constraint at FulfillmentService.cs:51-53 and uses sequential awaits for the same reason.
        var today = clock.ToLocalDate(clock.UtcNow);
        var computed = new List<RecipeBrowseRow>(allRecipes.Count);
        foreach (var r in allRecipes)
        {
            var recipeRatings = ratingsByRecipe.GetValueOrDefault(r.Id, []);
            computed.Add(await BuildRowAsync(r, recipesById, photoIds, recipeRatings, userId, today, ct));
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

    /// <summary>
    /// Expands one recipe against the pre-loaded <paramref name="recipesById"/> map and computes its
    /// expanded fulfillment + cost badges. Defensive fallback (recipe-composition.md Edge 1): if expansion
    /// fails because a sub id is absent from the non-archived map (a dangling/archived inclusion authored by a
    /// tampered request that bypassed the picker — N5 rules this out for legitimate recipes), THIS ONE ROW
    /// degrades to FLAT computation (no worse than before this change) rather than failing the whole page.
    /// </summary>
    private async Task<RecipeBrowseRow> BuildRowAsync(
        Recipe recipe,
        IReadOnlyDictionary<RecipeId, Recipe> recipesById,
        IReadOnlySet<RecipeId> photoIds,
        IReadOnlyList<RecipeRating> recipeRatings,
        Guid? userId,
        DateOnly today,
        CancellationToken ct)
    {
        var expandResult = await expansion.ExpandAsync(recipe.Id, recipesById, ct);
        if (expandResult.IsSuccess)
        {
            // Expanded path: aggregate the flat lines by (ProductId, UnitId) — duplicate subs (D14) merge —
            // and compute fulfillment/cost over the effective set at the recipe's own DefaultServings (Edge 2).
            var effectiveLines = expandResult.Value.AggregateByProductAndUnit();
            var fulfillmentResult = await fulfillment.ComputeExpandedAsync(
                effectiveLines, recipe.DefaultServings, recipe.DefaultServings, today, ct);
            var costResult = await costing.ComputeExpandedAsync(
                effectiveLines, recipe.DefaultServings, recipe.DefaultServings, ct);

            return BuildRow(
                recipe,
                fulfillmentResult.Overall,
                fulfillmentResult.Lines.Select(l => l.Status).ToList(),
                fulfillmentResult.Lines.Any(l => l.ExpiresWithinDays.HasValue),
                costResult,
                photoIds,
                recipeRatings,
                userId);
        }

        // Defensive flat fallback (Edge 1): compute over the recipe's own direct ingredients only.
        var flatFulfillment = await fulfillment.ComputeAsync(recipe, recipe.DefaultServings, today, ct);
        var flatCost = await costing.ComputeAsync(recipe, recipe.DefaultServings, ct);
        return BuildRow(
            recipe,
            flatFulfillment.Overall,
            flatFulfillment.Lines.Select(l => l.Status).ToList(),
            flatFulfillment.Lines.Any(l => l.ExpiresWithinDays.HasValue),
            flatCost,
            photoIds,
            recipeRatings,
            userId);
    }

    /// <summary>
    /// Assembles a <see cref="RecipeBrowseRow"/> from an already-computed per-line status set and cost.
    /// Shared by the expanded and flat-fallback paths (fed the expanded product-line statuses or the flat
    /// ingredient statuses respectively) so the badge maths cannot diverge between them.
    /// </summary>
    private static RecipeBrowseRow BuildRow(
        Recipe recipe,
        FulfillmentOverall overall,
        IReadOnlyList<IngredientStatus> statuses,
        bool hasExpiringSoon,
        CostPerServing costResult,
        IReadOnlySet<RecipeId> photoIds,
        IReadOnlyList<RecipeRating> recipeRatings,
        Guid? userId)
    {
        // inStock = lines that are InStock or Untracked (satisfied)
        var totalTracked = statuses.Count(s => s != IngredientStatus.Untracked);
        var inStock = statuses.Count(s => s is IngredientStatus.InStock or IngredientStatus.Untracked);

        // Fulfillment percentage (0–100): ratio of InStock+Untracked to all lines.
        // Recipes with no tracked ingredients are trivially 100% cookable.
        int pct;
        if (totalTracked == 0)
            pct = 100;
        else
            pct = (int)Math.Round((double)inStock / statuses.Count * 100);

        // Ratings (plantry-zlwp.1): MyStars from the caller's own row (null if unrated or unauthenticated);
        // HouseholdAvg/RatedCount over every rating, computed in-memory over the small pre-loaded set —
        // mirrors this query's own cost/fulfillment computation style rather than a SQL AVG()/COUNT().
        var myStars = userId is { } uid
            ? recipeRatings.FirstOrDefault(r => r.UserId == uid)?.Stars
            : null;
        var ratedCount = recipeRatings.Count;
        decimal? householdAvg = ratedCount > 0
            ? Math.Round(recipeRatings.Average(r => (decimal)r.Stars), 1)
            : null;

        return new RecipeBrowseRow(
            RecipeId: recipe.Id.Value,
            Name: recipe.Name,
            CookTimeMinutes: recipe.CookTimeMinutes,
            DefaultServings: recipe.DefaultServings,
            CreatedAt: recipe.CreatedAt,
            TagIds: recipe.Tags.Select(t => t.TagId.Value).ToList(),
            FullyCookable: overall.FullyCookable,
            FulfillmentPct: pct,
            InStockCount: inStock,
            TotalIngredientCount: statuses.Count,
            MissingCount: overall.MissingCount,
            HasIngredientExpiringSoon: hasExpiringSoon,
            CostPerServing: costResult.Completeness != CostCompleteness.None ? costResult.Amount : null,
            CostCompleteness: costResult.Completeness,
            HasPhoto: photoIds.Contains(recipe.Id),
            MyStars: myStars,
            HouseholdAvg: householdAvg,
            RatedCount: ratedCount
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
/// <param name="MyStars">The signed-in member's own 1-5 star rating; null if unrated (or no user was supplied).</param>
/// <param name="HouseholdAvg">Household average rating (1dp), null when <see cref="RatedCount"/> is zero.</param>
/// <param name="RatedCount">Count of household members who have rated this recipe.</param>
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
    bool HasPhoto,
    int? MyStars = null,
    decimal? HouseholdAvg = null,
    int RatedCount = 0);
