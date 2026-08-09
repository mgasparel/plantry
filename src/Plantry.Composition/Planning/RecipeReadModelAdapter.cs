using Microsoft.EntityFrameworkCore;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.MealPlanning;

/// <summary>
/// Web-side adapter for <see cref="IRecipeReadModel"/> — supplies the MealPlanning context with
/// recipe display facts from the Recipes context, over <see cref="RecipesDbContext"/>.
/// Also computes live fulfillment/cost enrichment by invoking Recipes' domain services
/// (<see cref="FulfillmentService"/> / <see cref="CostingService"/>) — MealPlanning borrows
/// these computations and rolls them up, never reimplements them (domain-model §1).
/// Lives in Plantry.Web (the composition root) to keep MealPlanning free of Recipes dependencies.
///
/// <para>The enrichment roll-up and ShopForWeek shortfall (J6) read a recipe's <b>expanded</b> view
/// (recipe-composition.md §7, D4) via <see cref="RecipeExpansionService"/>, so a dish that draws its
/// ingredients from included sub-recipes rolls up the SAME expanded cost/fulfillment/shortfall shown on the
/// recipe's Details page (J5) — no J5/J6 drift. A meal-plan week is low-N (≈7–21 dishes), so each dish is
/// expanded per call through the single repo-backed choke point. Defensive: if expansion fails because an
/// inclusion dangles (a tampered request that bypassed the picker — N5 blocks archiving an included recipe,
/// so this cannot happen for legitimate recipes), that dish degrades to FLAT computation rather than
/// disappearing from the plan.</para>
/// </summary>
public sealed class RecipeReadModelAdapter(
    RecipesDbContext db,
    RecipeExpansionService expansion,
    FulfillmentService fulfillmentService,
    CostingService costingService,
    IClock clock,
    IRecipeRatingRepository ratings,
    ICatalogProductReader catalog) : IRecipeReadModel
{
    public async Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default)
    {
        // Use the strongly-typed RecipeId so EF Core's value converter can translate the predicate.
        // Accessing .Value directly on a converted type in a LINQ predicate causes a translation
        // failure when combined with a HasQueryFilter that also uses a converted type.
        var rid = RecipeId.From(recipeId);
        // Project rather than load the entity: `r.Photo != null` becomes an EXISTS/JOIN, so the
        // (potentially large) photo bytes are never hydrated just to report presence.
        var row = await db.Recipes
            .Where(r => r.Id == rid && r.ArchivedAt == null)
            .Select(r => new
            {
                r.Id,
                r.Name,
                TagIds = r.Tags.Select(t => t.TagId.Value).ToList(),
                ProductIds = r.Ingredients.Select(i => i.ProductId).ToList(),
                r.DefaultServings,
                HasPhoto = r.Photo != null,
                r.CookTimeMinutes,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var models = await BuildReadModelsAsync(
        [
            new RecipeProjection(
                row.Id.Value,
                row.Name,
                row.TagIds,
                row.ProductIds,
                row.DefaultServings,
                row.HasPhoto,
                row.CookTimeMinutes),
        ], ct);
        return models.GetValueOrDefault(row.Id.Value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, RecipeReadModel>> GetByIdsAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default)
    {
        if (recipeIds.Count == 0) return new Dictionary<Guid, RecipeReadModel>();

        // Use the strongly-typed RecipeId set in the predicate — same rationale as GetByIdAsync
        // above: accessing .Value directly on a converted-type column in a LINQ predicate fails to
        // translate when combined with this DbContext's HouseholdId-based HasQueryFilter.
        var ids = recipeIds.Select(RecipeId.From).ToHashSet();
        var rows = await db.Recipes
            .Where(r => r.ArchivedAt == null && ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Name,
                TagIds = r.Tags.Select(t => t.TagId.Value).ToList(),
                ProductIds = r.Ingredients.Select(i => i.ProductId).ToList(),
                r.DefaultServings,
                HasPhoto = r.Photo != null,
                r.CookTimeMinutes,
            })
            .ToListAsync(ct);

        return await BuildReadModelsAsync(rows.Select(r => new RecipeProjection(
            r.Id.Value,
            r.Name,
            r.TagIds,
            r.ProductIds,
            r.DefaultServings,
            r.HasPhoto,
            r.CookTimeMinutes)).ToList(), ct);
    }

    public async Task<IReadOnlyList<RecipeReadModel>> SearchAsync(
        string nameQuery, int maxResults = 20, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(nameQuery) ? "" : nameQuery.Trim();

        var rows = await db.Recipes
            .Where(r => r.ArchivedAt == null &&
                        (q == "" || EF.Functions.ILike(r.Name, $"%{q}%")))
            .OrderBy(r => r.Name)
            .Take(maxResults)
            .Select(r => new
            {
                r.Id,
                r.Name,
                TagIds = r.Tags.Select(t => t.TagId.Value).ToList(),
                ProductIds = r.Ingredients.Select(i => i.ProductId).ToList(),
                r.DefaultServings,
                HasPhoto = r.Photo != null,
                r.CookTimeMinutes,
            })
            .ToListAsync(ct);

        var models = await BuildReadModelsAsync(rows.Select(r => new RecipeProjection(
            r.Id.Value,
            r.Name,
            r.TagIds,
            r.ProductIds,
            r.DefaultServings,
            r.HasPhoto,
            r.CookTimeMinutes)).ToList(), ct);
        return rows.Select(r => models[r.Id.Value]).ToList();
    }

    /// <inheritdoc />
    public async Task<RecipeDishEnrichment?> GetEnrichmentAsync(
        Guid recipeId,
        int servings,
        DateOnly today,
        CancellationToken ct = default)
    {
        var rid = RecipeId.From(recipeId);
        var recipe = await db.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ArchivedAt == null, ct);

        if (recipe is null) return null;

        // Borrow Recipes' domain services — MealPlanning rolls up, never recomputes. Expand to the flat
        // product-level view (D4 choke point) so included recipes' products are reflected; degrade to flat
        // on the degenerate dangling-inclusion case (see class remarks).
        var expandResult = await expansion.ExpandAsync(rid, ct);

        IReadOnlyList<IngredientStatus> statuses;
        bool hasExpiring;
        bool hasContributingExpiringStock;
        CostPerServing cost;
        if (expandResult.IsSuccess)
        {
            var effectiveLines = expandResult.Value.AggregateByProductAndUnit();
            var fulfillment = await fulfillmentService.ComputeExpandedAsync(
                effectiveLines, recipe.DefaultServings, servings, today, ct);
            cost = await costingService.ComputeExpandedAsync(effectiveLines, recipe.DefaultServings, servings, ct);
            statuses = fulfillment.Lines.Select(l => l.Status).ToList();
            hasExpiring = fulfillment.Lines.Any(l => l.ExpiresWithinDays.HasValue);
            hasContributingExpiringStock = fulfillment.Lines.Any(l => l.HasContributingExpiringStock);
        }
        else
        {
            var fulfillment = await fulfillmentService.ComputeAsync(recipe, servings, today, ct);
            cost = await costingService.ComputeAsync(recipe, servings, ct);
            statuses = fulfillment.Lines.Select(l => l.Status).ToList();
            hasExpiring = fulfillment.Lines.Any(l => l.ExpiresWithinDays.HasValue);
            hasContributingExpiringStock = fulfillment.Lines.Any(l => l.HasContributingExpiringStock);
        }

        // Compute fulfillment % from the (expanded or flat) line-level results.
        // Untracked staples are excluded (always satisfied, C12). Only tracked lines contribute.
        var trackedCount = statuses.Count(s => s != IngredientStatus.Untracked);

        int pct;
        if (trackedCount == 0)
        {
            // No tracked ingredients → treat as 100% (untracked-only recipe is always cookable).
            pct = 100;
        }
        else
        {
            // InStockViaSubstitute (plantry-aqpa.2) counts as fully satisfied here too — no shopping
            // action needed, only the per-row display distinguishes how it was satisfied.
            var inStockCount = statuses.Count(s => s is IngredientStatus.InStock or IngredientStatus.InStockViaSubstitute);
            pct = (int)Math.Round(100.0 * inStockCount / trackedCount);
        }

        // TotalCost = CostPerServing.Amount × servings (Amount is per-serving; we want the total).
        decimal? totalCost = cost.Amount.HasValue ? cost.Amount.Value * servings : null;

        // No tracked line means there was no expiry-bearing inventory fact to evaluate. Preserve
        // that as unknown for planning rather than collapsing it to "no expiring stock"; a tracked
        // line with no qualifying allocation is the distinct, known-false state.
        bool? planningHasContributingExpiringStock = statuses.Any(s => s != IngredientStatus.Untracked)
            ? hasContributingExpiringStock
            : null;

        return new RecipeDishEnrichment(
            pct,
            totalCost,
            cost.Completeness == Plantry.Recipes.Domain.CostCompleteness.Partial,
            hasExpiring,
            planningHasContributingExpiringStock);
    }

    /// <summary>
    /// Populates the Planning candidate-evidence contract at the composition boundary. Generate-plan
    /// calls this once for its bounded candidate snapshot and reuses the returned facts across every
    /// slot; the adapter keeps Recipes/Fulfillment/Costing ownership here rather than letting Planning
    /// reach into those contexts.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>> GetCandidateEvidenceAsync(
        IReadOnlyCollection<CandidateRecipeEvidenceRequest> requests,
        DateOnly today,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, CandidateRecipeEvidence>();
        foreach (var request in requests.GroupBy(r => r.RecipeId).Select(g => g.First()))
        {
            var enrichment = await GetEnrichmentAsync(request.RecipeId, request.Servings, today, ct);
            if (enrichment is null) continue;

            var completeness = enrichment.CostIsPartial
                ? Plantry.Planning.Domain.CandidateCostCompleteness.Partial
                : enrichment.TotalCost.HasValue
                    ? Plantry.Planning.Domain.CandidateCostCompleteness.Complete
                    : Plantry.Planning.Domain.CandidateCostCompleteness.Unknown;
            decimal? costPerServing = enrichment.TotalCost is { } totalCost && request.Servings > 0
                ? totalCost / request.Servings
                : null;

            result[request.RecipeId] = new CandidateRecipeEvidence(
                costPerServing,
                completeness,
                enrichment.FulfillmentPercent,
                enrichment.HasContributingExpiringStock);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Guid>> FindSoleYieldPhotoRecipeIdsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, Guid>();

        // YieldProductId is a plain (unconverted) Guid? column (RecipesDbContext.cs:56), unlike the
        // RecipeId/ProductId value-object keys elsewhere in this adapter — a straight HashSet<Guid>
        // .Contains translates without the value-object EF-translation caveat those need.
        var ids = productIds.ToHashSet();
        var rows = await db.Recipes
            .Where(r => r.ArchivedAt == null && r.YieldProductId != null && ids.Contains(r.YieldProductId.Value))
            .Select(r => new { ProductId = r.YieldProductId!.Value, r.Id, HasPhoto = r.Photo != null })
            .ToListAsync(ct);

        // Group by producer product, keep only the unambiguous (exactly one producer-recipe) groups,
        // then require that sole recipe to have a photo — collapses zero/many/no-photo to "absent".
        return rows
            .GroupBy(r => r.ProductId)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .Where(r => r.HasPhoto)
            .ToDictionary(r => r.ProductId, r => r.Id.Value);
    }

    /// <inheritdoc />
    public async Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default)
    {
        // Targeted full-corpus query: does ANY non-archived recipe carry this tag?
        // Never filtered by the 50-cap candidate list from SearchAsync.
        var tid = TagId.From(tagId);
        return await db.Recipes
            .Where(r => r.ArchivedAt == null)
            .AnyAsync(r => r.Tags.Any(t => t.TagId == tid), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(
        Guid recipeId,
        int servings,
        CancellationToken ct = default)
    {
        var rid = RecipeId.From(recipeId);
        var recipe = await db.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == rid && r.ArchivedAt == null, ct);

        if (recipe is null) return [];

        var today = clock.ToLocalDate(clock.UtcNow);

        // Expand to the flat product-level view (D4 choke point) and compute shortfall over the expanded set,
        // so ShopForWeek buys for included recipes' products too. Delegate to the shared shortfall calculator
        // (Missing + Low, shortfall = scaledRequired − available) so this path and AddMissingToShoppingList
        // (J5) cannot diverge. Degrade to flat on the degenerate dangling-inclusion case (see class remarks).
        var expandResult = await expansion.ExpandAsync(rid, ct);

        IReadOnlyList<IngredientShortfall> shortfallLines;
        if (expandResult.IsSuccess)
        {
            var effectiveLines = expandResult.Value.AggregateByProductAndUnit();
            var fulfillment = await fulfillmentService.ComputeExpandedAsync(
                effectiveLines, recipe.DefaultServings, servings, today, ct);
            shortfallLines = RecipeShortfallCalculator.Compute(
                effectiveLines, fulfillment, recipe.DefaultServings, servings);
        }
        else
        {
            var fulfillment = await fulfillmentService.ComputeAsync(recipe, servings, today, ct);
            shortfallLines = RecipeShortfallCalculator.Compute(recipe, fulfillment, servings);
        }

        // Exclude home-produced products (Product.IsProduced — a recipe yield, cook leftover, or garden
        // produce) from the week's shopping list: the household makes these at home, so suggesting a
        // purchase is wrong by definition (plantry-4osq). RecipeShortfallCalculator stays a pure
        // fulfillment→shortfall transform with no catalog dependency (the ticket's stated lock); the
        // purchasability filter lives here in the Composition adapter instead — mirroring 4ac08dee's
        // ShoppingPantryReaderAdapter.AggregateStockLevelsAsync(excludeProduced: true) placement. A
        // product id absent from the resolved summaries (unknown to this household's catalog) is kept —
        // matches the "unresolvable → not excluded" default used elsewhere in this adapter.
        if (shortfallLines.Count == 0)
            return [];

        var candidateIds = shortfallLines.Select(s => s.ProductId).Distinct().ToList();
        var summaries = await catalog.ResolveSummariesAsync(candidateIds, ct);
        shortfallLines = shortfallLines
            .Where(s => !summaries.TryGetValue(s.ProductId, out var summary) || !summary.IsProduced)
            .ToList();

        return shortfallLines
            .Select(s => new RecipeMissingIngredient(s.ProductId, s.ShortfallQuantity, s.UnitId))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, RecipeRatingSummary>> GetRatingSummariesAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default)
    {
        if (recipeIds.Count == 0) return new Dictionary<Guid, RecipeRatingSummary>();

        var ids = recipeIds.Select(RecipeId.From).ToList();
        var rows = await ratings.ListByRecipeIdsAsync(ids, ct);

        // Mirrors BrowseRecipesQuery's MyStars/HouseholdAvg/RatedCount math (plantry-zlwp.1) — same
        // Math.Round(..., 1) convention so the planner's household-average signal never drifts from
        // what the household sees on Browse/Details for the same recipe.
        return rows
            .GroupBy(r => r.RecipeId.Value)
            .ToDictionary(
                g => g.Key,
                g => new RecipeRatingSummary(
                    StarsByUserId: g.ToDictionary(r => r.UserId, r => r.Stars),
                    HouseholdAvg: Math.Round(g.Average(r => (decimal)r.Stars), 1),
                    RatedCount: g.Count()));
    }

    /// <summary>
    /// Enriches one bounded recipe projection set with semantic tag facts and compact diversity profiles.
    /// Tag vocabulary and Catalog ingredient names are each loaded once for the whole set; no LLM is called
    /// and no classification is persisted. Planning receives only its own contract types.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, RecipeReadModel>> BuildReadModelsAsync(
        IReadOnlyList<RecipeProjection> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return new Dictionary<Guid, RecipeReadModel>();

        // Household tags are small reference data. Load once so applied archived tags still retain their
        // display/category facts, while only active tags participate in a missing-facet fallback match.
        var tagRows = await db.Tags
            .Select(t => new
            {
                Id = t.Id.Value,
                t.Name,
                t.Category,
                IsArchived = t.ArchivedAt != null,
            })
            .ToListAsync(ct);
        var tagsById = tagRows.ToDictionary(t => t.Id);
        var activeVocabulary = tagRows
            .Where(t => !t.IsArchived)
            .Select(t => new RecipeSemanticTagFact(t.Id, t.Name, MapCategory(t.Category)))
            .OrderBy(t => t.TagId)
            .ToList();

        var productIds = rows
            .SelectMany(r => r.ProductIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var productsById = await catalog.ResolveSummariesAsync(productIds, ct);

        return rows.ToDictionary(
            row => row.RecipeId,
            row =>
            {
                var tagFacts = row.TagIds
                    .Distinct()
                    .Where(tagsById.ContainsKey)
                    .Select(id => tagsById[id])
                    .Select(t => new RecipeSemanticTagFact(t.Id, t.Name, MapCategory(t.Category)))
                    .OrderBy(t => t.TagId)
                    .ToList();
                var ingredientFacts = row.ProductIds
                    .Distinct()
                    .Where(productsById.ContainsKey)
                    .Select(id => productsById[id])
                    .Select(p => new RecipeIngredientFact(p.Id, p.Name))
                    .OrderBy(p => p.ProductId)
                    .ToList();
                var profile = RecipeDiversityProfile.Create(
                    row.RecipeId,
                    row.Name,
                    tagFacts,
                    activeVocabulary,
                    ingredientFacts);

                return new RecipeReadModel(
                    row.RecipeId,
                    row.Name,
                    row.TagIds,
                    row.DefaultServings,
                    row.HasPhoto,
                    row.CookTimeMinutes,
                    tagFacts,
                    profile);
            });
    }

    private static RecipeSemanticTagCategory? MapCategory(TagCategory? category) => category switch
    {
        TagCategory.Diet => RecipeSemanticTagCategory.Diet,
        TagCategory.Protein => RecipeSemanticTagCategory.Protein,
        TagCategory.Flavor => RecipeSemanticTagCategory.Flavor,
        TagCategory.Cuisine => RecipeSemanticTagCategory.Cuisine,
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    private sealed record RecipeProjection(
        Guid RecipeId,
        string Name,
        IReadOnlyList<Guid> TagIds,
        IReadOnlyList<Guid> ProductIds,
        int DefaultServings,
        bool HasPhoto,
        int? CookTimeMinutes);
}
