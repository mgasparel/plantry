using Plantry.Planning.Domain;

namespace Plantry.Planning.Application;

/// <summary>
/// Anti-corruption read port onto the Recipes context for the MealPlanning context.
/// Supplies the minimal facts needed to display and validate a recipe dish.
/// Implemented in Plantry.Web over RecipesDbContext.
/// </summary>
public interface IRecipeReadModel
{
    /// <summary>
    /// Returns a recipe summary for display in the meal editor.
    /// Returns null when the recipe does not exist in this household.
    /// </summary>
    Task<RecipeReadModel?> GetByIdAsync(Guid recipeId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the recipe search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> recipes whose name contains the query.
    /// Empty/whitespace query returns a short list of all recipes.
    /// </summary>
    Task<IReadOnlyList<RecipeReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);

    /// <summary>Loads the complete active household recipe corpus for generation feasibility and shortlisting.</summary>
    async Task<IReadOnlyList<RecipeReadModel>> LoadActiveCorpusAsync(CancellationToken ct = default) =>
        await SearchAsync(string.Empty, int.MaxValue, ct);



    /// <summary>
    /// Returns live fulfillment and cost facts for a recipe at the given serving count.
    /// Computed fresh by Recipes' domain services (FulfillmentService / CostingService) via the
    /// Inventory and Pricing ports — MealPlanning borrows these, never recomputes them (domain-model §1).
    /// Returns null when the recipe does not exist in this household.
    /// </summary>
    Task<RecipeDishEnrichment?> GetEnrichmentAsync(
        Guid recipeId,
        int servings,
        DateOnly today,
        CancellationToken ct = default);

    /// <summary>
    /// Returns one bounded snapshot of the live evidence used to rank a candidate recipe set. The
    /// caller invokes this once per generate request and reuses the result for every empty slot, so
    /// candidate enrichment is not repeated per slot. The default implementation preserves compatibility
    /// with existing read-model doubles by adapting the existing enrichment port; a composition adapter
    /// may override it when it can batch the underlying Recipes/Pricing/Inventory reads.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, CandidateRecipeEvidence>> GetCandidateEvidenceAsync(
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
                ? CandidateCostCompleteness.Partial
                : enrichment.TotalCost.HasValue
                    ? CandidateCostCompleteness.Complete
                    : CandidateCostCompleteness.Unknown;
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

    /// <summary>
    /// Returns the ingredients that are Missing or Low at <paramref name="servings"/> for a recipe.
    /// Used by ShopForWeek to aggregate the shopping list (J6).
    /// Returns an empty list when the recipe does not exist, has no ingredients, or everything is in stock.
    /// </summary>
    Task<IReadOnlyList<RecipeMissingIngredient>> GetMissingIngredientsAsync(
        Guid recipeId,
        int servings,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> when ANY recipe in the household's full recipe corpus carries
    /// <paramref name="tagId"/> — regardless of the 50-cap candidate list from <see cref="SearchAsync"/>.
    /// Used by <see cref="Plantry.Planning.Domain.UnfulfillabilityDetector"/> for feasibility
    /// pre-checks: a confident "you have no vegetarian recipes" would be wrong if recipes outside
    /// the top-50 carry the tag. This is a targeted, cheap corpus query.
    /// Returns <see langword="false"/> when no non-archived recipe carries the tag.
    /// </summary>
    Task<bool> AnyRecipeWithTagAsync(Guid tagId, CancellationToken ct = default);

    /// <summary>
    /// Batched lookup for the meal-plan card's product-dish photo inheritance (plantry-f4dt): for each
    /// product id, resolves the id of the recipe to borrow a photo from — a product-dish tile shows a
    /// recipe's photo (soft, never duplicated — resolved live on every call) only when EXACTLY ONE
    /// non-archived recipe declares that product as its cook yield (<see cref="RecipeReadModel"/>'s
    /// <c>YieldProductId</c> concept, "Made by" on Product Detail — recipe-composition.md §9) AND that
    /// recipe has a stored photo. A product id is OMITTED from the returned dictionary — never mapped
    /// to a default/sentinel — when it has zero producer-recipes, more than one (genuinely ambiguous:
    /// a household can point a second <c>AuthorRecipe</c> yield at an existing product), or exactly one
    /// producer-recipe with no photo; the caller's <c>GetValueOrDefault</c> then falls through to the
    /// existing gradient placeholder in all three cases without needing to distinguish them.
    /// Default implementation returns an empty dictionary (no photo inheritance) so existing
    /// <see cref="IRecipeReadModel"/> test doubles do not need to implement this to keep compiling —
    /// override in any double that specifically exercises product-dish photo inheritance.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> FindSoleYieldPhotoRecipeIdsAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());

    /// <summary>
    /// Batched lookup for a set of recipe ids in a single round-trip (plantry-r2yf) — the shape
    /// Today's <c>LoadPlannedMealsTodayAsync</c> uses to resolve every recipe dish across the whole
    /// day up front, mirroring the week-wide pre-passes established for product-dish resolution
    /// (plantry-nlg4/plantry-vj6z). Ids that do not exist (or belong to another household) are
    /// simply omitted from the result — same "absent means unresolved" convention as
    /// <see cref="FindSoleYieldPhotoRecipeIdsAsync"/>.
    /// Default implementation falls back to one <see cref="GetByIdAsync"/> call per id, using the
    /// same default-interface-implementation pattern as <see cref="FindSoleYieldPhotoRecipeIdsAsync"/>
    /// so existing <see cref="IRecipeReadModel"/> test doubles keep compiling (and keep behaving
    /// correctly) without overriding this member — the production adapter overrides it with a
    /// genuinely batched query.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, RecipeReadModel>> GetByIdsAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, RecipeReadModel>();
        foreach (var id in recipeIds)
        {
            var model = await GetByIdAsync(id, ct);
            if (model is not null)
                result[id] = model;
        }
        return result;
    }

    /// <summary>
    /// Batched lookup of household-wide rating signal for a set of recipe ids (plantry-zlwp.5) — feeds
    /// <c>GeneratePlanService</c>'s per-slot <see cref="Plantry.Planning.Domain.CandidateRecipe"/>
    /// enrichment ("same seam as IRecipeReadModel / HouseholdMemberReaderAdapter" per the issue).
    /// The per-user stars in each <see cref="RecipeRatingSummary"/> are household-wide (every rater, not
    /// just a given slot's attendees) — the caller narrows to the slot's <c>DefaultAttendees</c> when
    /// building <c>CandidateRecipe.AttendeeStars</c>. A recipe id with no ratings is simply OMITTED from
    /// the result (same "absent means unresolved" convention as <see cref="GetByIdsAsync"/> and
    /// <see cref="FindSoleYieldPhotoRecipeIdsAsync"/>) — callers fall back to "no rating data" rather than
    /// a default/sentinel summary.
    /// Default implementation returns an empty dictionary (no rating data) so existing
    /// <see cref="IRecipeReadModel"/> test doubles do not need to implement this to keep compiling —
    /// override in any double that specifically exercises rating-aware planning.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, RecipeRatingSummary>> GetRatingSummariesAsync(
        IReadOnlyCollection<Guid> recipeIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, RecipeRatingSummary>>(new Dictionary<Guid, RecipeRatingSummary>());
}

/// <summary>Display facts for a recipe in the meal editor.</summary>
/// <param name="HasPhoto">True when the recipe has a stored photo (served at
/// <c>/Recipes/{RecipeId}?handler=Photo</c>) — lets the dish picker show a thumbnail
/// and fall back to an initial chip when absent.</param>
/// <param name="CookTimeMinutes">Cook time in minutes; null when not set. Added for plantry-r2yf so
/// Today's planned-meals band can resolve cook time from this port instead of reaching past it into
/// the Recipes domain repository (composition-root bypass, formerly justified by ADR-021 §3) —
/// the port's contract is "minimal facts needed to display and validate a recipe dish"
/// (see this interface's summary), and cook time is such a fact. Defaults to null so existing
/// positional construction sites keep compiling; the adapter always supplies the resolved value.</param>
/// <param name="TagFacts">
/// Stable id + display-name + category facts for the recipe's tags. These are additive to
/// <paramref name="TagIds"/>: hard constraints continue to validate against ids, while selection can
/// reason over names/categories without reaching through this port into Recipes persistence.
/// </param>
/// <param name="DiversityProfile">
/// Compact confirmed/fallback semantic facts for deterministic recipe comparison. Null is the graceful
/// compatibility state for a read-model implementation that has not supplied profile facts.
/// </param>
public sealed record RecipeReadModel(
    Guid RecipeId,
    string Name,
    IReadOnlyList<Guid> TagIds,
    int DefaultServings,
    bool HasPhoto = false,
    int? CookTimeMinutes = null,
    IReadOnlyList<RecipeSemanticTagFact>? TagFacts = null,
    RecipeDiversityProfile? DiversityProfile = null);

/// <summary>One recipe/serving request in a candidate evidence snapshot.</summary>
public sealed record CandidateRecipeEvidenceRequest(Guid RecipeId, int Servings);

/// <summary>
/// Planning-owned live evidence for one candidate. Cost is per serving; partial cost is explicitly marked
/// as an under-estimate and unknown cost remains null. The expiring-stock flag is already allocated at the
/// Recipes boundary, so on-hand stock that would not be consumed does not masquerade as waste evidence.
/// </summary>
public sealed record CandidateRecipeEvidence(
    decimal? CostPerServing,
    CandidateCostCompleteness CostCompleteness,
    int FulfillmentPercent,
    bool? HasContributingExpiringStock);

/// <summary>
/// Live fulfillment and cost enrichment for a recipe dish at a given serving count.
/// Borrowed from Recipes' read models — MealPlanning rolls these up, never recomputes (domain-model §1).
/// </summary>
/// <param name="FulfillmentPercent">
/// 0–100 percentage of tracked ingredients that are fully In Stock at the requested servings.
/// 100 = fully cookable; 0 = nothing in stock. Untracked staples are excluded (C12).
/// </param>
/// <param name="TotalCost">
/// Estimated total cost for all servings; null when no ingredients have pricing data.
/// </param>
/// <param name="CostIsPartial">
/// True when the cost estimate covers only some ingredients (partial pricing data).
/// </param>
/// <param name="HasExpiringIngredients">
/// True when any tracked ingredient has stock expiring within 4 days ("Use soon" flag, J1 step 4).
/// </param>
/// <param name="HasContributingExpiringStock">
/// True only when positive stock allocated to satisfy a tracked ingredient has a non-expired expiry
/// within the configured horizon. False means tracked lines were evaluated but no such allocation was
/// found. Null means the recipe had no tracked line from which expiry evidence could be obtained (for
/// example, an all-untracked recipe); the planner renders that state as unknown rather than none.
/// </param>
public sealed record RecipeDishEnrichment(
    int FulfillmentPercent,
    decimal? TotalCost,
    bool CostIsPartial,
    bool HasExpiringIngredients,
    bool? HasContributingExpiringStock = null);

/// <summary>
/// One missing (or low-stock) ingredient for a recipe at a given serving count, for the ShopForWeek flow.
/// Untracked staples are never included (C12 — always satisfied).
/// </summary>
/// <param name="ProductId">Soft ref → catalog.product (DM-3).</param>
/// <param name="Quantity">
/// Shortfall quantity — max(0, scaledRequired − available) — what the household still needs to buy.
/// For Missing lines (zero available) this equals the full scaled required quantity.
/// </param>
/// <param name="UnitId">Soft ref → catalog.unit (DM-3).</param>
public sealed record RecipeMissingIngredient(Guid ProductId, decimal Quantity, Guid UnitId);

/// <summary>
/// Household-wide rating signal for one recipe (plantry-zlwp.5) — the raw facts
/// <see cref="Plantry.Planning.Domain.CandidateRecipe"/>'s per-slot enrichment is built from.
/// Mirrors <c>BrowseRecipesQuery</c>'s MyStars/HouseholdAvg/RatedCount shape, minus MyStars (the
/// planner has no single "current user" — it enriches per slot attendee instead).
/// </summary>
/// <param name="StarsByUserId">Every household rater's 1-5 stars for this recipe, keyed by user id.</param>
/// <param name="HouseholdAvg">Average of every rater's stars, rounded to 1dp; null when nobody has rated.</param>
/// <param name="RatedCount">Count of household members who have rated this recipe.</param>
public sealed record RecipeRatingSummary(
    IReadOnlyDictionary<Guid, int> StarsByUserId,
    decimal? HouseholdAvg,
    int RatedCount);
