using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Shopping.Application;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// Read-only Detail page for a saved <see cref="Recipe"/> (recipes-journeys.md J6 step 10 / J7 step 8).
/// Loads the recipe with its ingredient list, tag membership, and optional photo. Product names are
/// resolved via <see cref="ICatalogProductReader"/>; tag names via <see cref="ITagRepository"/>.
/// Directions are derived into steps/sections at render (C13) — not persisted as rows.
/// Live fulfillment (P2-2a) and cost (P2-2b) are computed at default servings and passed to the view.
/// </summary>
[Authorize]
public sealed class DetailsModel(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader catalog,
    FulfillmentService fulfillmentService,
    CostingService costingService,
    AddMissingToShoppingList addMissingService,
    AddIngredientsToShoppingList addAllService,
    ShoppingListQueryService shoppingList) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public RecipeDetailView Recipe { get; private set; } = null!;

    /// <summary>
    /// The fulfilment rail card view model rendered on first page load (at default servings), including
    /// the server-computed "Add missing" / "Add all" button label states (plantry-gsj). The servings
    /// stepper swaps this via <see cref="OnGetFulfilmentAsync"/> and the add buttons swap it via the
    /// POST handlers, so the buttons always reflect server truth (no client-side flag flip).
    /// </summary>
    public DetailsFulfilmentCardModel FulfilmentCard { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return NotFound();

        // Batch-resolve the ingredient list in a fixed number of round-trips (no per-row N+1):
        // one query for product display facts, one for the unit codes the quantities render with.
        var productIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
        var productLookup = await catalog.ResolveSummariesAsync(productIds, ct);

        var unitIds = recipe.Ingredients
            .Where(i => i.UnitId is not null)
            .Select(i => i.UnitId!.Value)
            .Distinct()
            .ToList();
        var unitLookup = await catalog.ResolveUnitCodesAsync(unitIds, ct);

        // Resolve tag names via the in-context tag repository.
        var tagIds = recipe.Tags.Select(rt => rt.TagId).ToList();
        var tagLookup = await tags.ResolveNamesAsync(tagIds, ct);

        // Compute live fulfillment and cost at default servings (P2-2a / P2-2b).
        // Sequential awaits required — FulfillmentService and CostingService share the scoped EF
        // DbContexts; concurrent Task.WhenAll would throw InvalidOperationException (see BrowseRecipesQuery.cs:51-53).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, recipe.DefaultServings, today, ct);
        var cost = await costingService.ComputeAsync(recipe, recipe.DefaultServings, ct);

        // Server-compute the add-to-shopping button label states from the true delta between the
        // recipe's shortfall/required sets and its existing contribution slice (plantry-gsj).
        FulfilmentCard = await BuildCardModelAsync(
            recipe, recipe.DefaultServings, productLookup, unitLookup, fulfillment, oob: false, summary: null, ct);

        Recipe = RecipeDetailView.From(recipe, productLookup, unitLookup, tagLookup, ParseDirections(recipe.Directions), fulfillment, cost);
        return Page();
    }

    /// <summary>
    /// htmx GET handler for the fulfillment rail card partial (recipes-journeys.md J3).
    /// Called by the servings stepper on the Detail page whenever servings changes; returns
    /// the <c>_DetailsFulfilmentCard</c> partial recomputed at <paramref name="servings"/>.
    /// The partial includes an OOB swap for <c>#rd-ing-rows</c> so per-ingredient status dots
    /// and sub-labels also update without a full-page reload.
    /// Returns 404 when the recipe does not exist; returns the partial at DefaultServings
    /// when <paramref name="servings"/> is out of range (≤0 or >24).
    /// </summary>
    public async Task<IActionResult> OnGetFulfilmentAsync(int servings, CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return NotFound();

        // Clamp requested servings to a valid range (mirrors the stepper bounds in the view).
        var desiredServings = servings is > 0 and <= 24 ? servings : recipe.DefaultServings;

        var (productLookup, unitLookup, fulfillment) = await ResolveFulfilmentAsync(recipe, desiredServings, ct);

        // Oob: true so the partial's #rd-ing-rows block carries hx-swap-oob="true" and htmx
        // replaces the ingredient rows in place alongside the primary #rd-fulf-card swap. Recomputing
        // the button label at the new servings gives the top-up state ("Add N more") rather than a
        // blind full re-add when servings grew (plantry-gsj).
        var vm = await BuildCardModelAsync(
            recipe, desiredServings, productLookup, unitLookup, fulfillment, oob: true, summary: null, ct);

        return Partial("_DetailsFulfilmentCard", vm);
    }

    /// <summary>
    /// Resolves the catalog lookups and the fresh fulfilment result for a recipe at a given serving
    /// count — the shared read every card render needs (initial load, servings refresh, post-add swap).
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, CatalogProductSummary> Products,
                        IReadOnlyDictionary<Guid, string> Units,
                        FulfillmentResult Fulfillment)> ResolveFulfilmentAsync(
        Recipe recipe, int servings, CancellationToken ct)
    {
        var productIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
        var productLookup = await catalog.ResolveSummariesAsync(productIds, ct);

        var unitIds = recipe.Ingredients
            .Where(i => i.UnitId is not null)
            .Select(i => i.UnitId!.Value)
            .Distinct()
            .ToList();
        var unitLookup = await catalog.ResolveUnitCodesAsync(unitIds, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, servings, today, ct);
        return (productLookup, unitLookup, fulfillment);
    }

    /// <summary>
    /// Builds the fulfilment card view model — the per-ingredient rows plus the server-computed
    /// "Add missing" / "Add all" button label states (plantry-gsj). The label states come from the
    /// true delta between the recipe's shortfall/required target sets at <paramref name="servings"/>
    /// and its existing contribution slice, read via Shopping's public query surface (the Web page is
    /// the composition root, mirroring plantry-yt0m). <paramref name="summary"/> is non-null only on a
    /// post-add re-render, surfacing "Added X · Y already on your list · Z checked off".
    /// </summary>
    private async Task<DetailsFulfilmentCardModel> BuildCardModelAsync(
        Recipe recipe,
        int servings,
        IReadOnlyDictionary<Guid, CatalogProductSummary> productLookup,
        IReadOnlyDictionary<Guid, string> unitLookup,
        FulfillmentResult fulfillment,
        bool oob,
        ShoppingSyncOutcome? summary,
        CancellationToken ct)
    {
        var ingredientGroups = BuildIngredientGroups(recipe, productLookup, unitLookup, fulfillment);

        // Target sets computed via the same shared calculator the write path uses, so the label and the
        // synced set cannot drift (plantry-gsj).
        var trackedProductIds = productLookup.Where(kv => kv.Value.TrackStock).Select(kv => kv.Key).ToHashSet();
        var missingTargets = RecipeShoppingTargets.Missing(recipe, fulfillment, servings);
        var allTargets = RecipeShoppingTargets.All(recipe, trackedProductIds, servings);

        var contribution = await shoppingList.GetRecipeContributionStateAsync(recipe.Id.Value, ct);
        var missingLabel = AddToShoppingLabelCalculator.Compute(
            missingTargets, contribution.ContributedByProduct, contribution.CheckedOffProducts);
        var allLabel = AddToShoppingLabelCalculator.Compute(
            allTargets, contribution.ContributedByProduct, contribution.CheckedOffProducts);

        return new DetailsFulfilmentCardModel(
            RecipeId: recipe.Id.Value,
            DefaultServings: recipe.DefaultServings,
            Fulfillment: fulfillment,
            IngredientGroups: ingredientGroups,
            Oob: oob,
            MissingButton: new AddButtonView(missingTargets.Count > 0, missingLabel.State, missingLabel.PendingLines),
            AllButton: new AddButtonView(allTargets.Count > 0, allLabel.State, allLabel.PendingLines),
            Summary: summary);
    }

    /// <summary>
    /// Builds the fulfilment-keyed ingredient group list (per-ingredient status dots + sub-labels).
    /// Shared by the full-page view (<see cref="RecipeDetailView.From"/>) and the card partial so the
    /// grouping/status logic lives in one place.
    /// </summary>
    internal static IReadOnlyList<IngredientGroupView> BuildIngredientGroups(
        Recipe recipe,
        IReadOnlyDictionary<Guid, CatalogProductSummary> productLookup,
        IReadOnlyDictionary<Guid, string> unitLookup,
        FulfillmentResult fulfillment)
    {
        var fulfillmentByIngredientId = fulfillment.Lines.ToDictionary(l => l.IngredientId, l => l);
        return recipe.Ingredients
            .OrderBy(i => i.Ordinal)
            .GroupBy(i => i.GroupHeading)
            .Select(g => new IngredientGroupView(
                Heading: g.Key,
                Items: g.Select(i =>
                {
                    productLookup.TryGetValue(i.ProductId, out var product);
                    var isUntracked = product is { TrackStock: false };
                    var unitCode = !isUntracked && i.UnitId is { } unitId
                        ? unitLookup.GetValueOrDefault(unitId)
                        : null;
                    fulfillmentByIngredientId.TryGetValue(i.Id, out var ingFulfillment);
                    return new IngredientItemView(
                        ProductName: product?.Name ?? "(unknown product)",
                        Quantity: isUntracked ? null : i.Quantity,
                        UnitCode: unitCode,
                        IsUntracked: isUntracked,
                        Status: ingFulfillment?.Status ?? IngredientStatus.Untracked,
                        ExpiresWithinDays: ingFulfillment?.ExpiresWithinDays);
                }).ToList()))
            .ToList();
    }

    /// <summary>
    /// Streams the recipe photo bytes with the stored content type (mirrors how Intake serves
    /// <c>import_receipt</c> bytes — recipes-domain-model.md Resolved call 3).
    /// Returns 404 when the recipe has no photo or the recipe itself does not exist.
    /// </summary>
    public async Task<IActionResult> OnGetPhotoAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe?.Photo is null) return NotFound();
        return File(recipe.Photo.Content, recipe.Photo.ContentType);
    }

    /// <summary>
    /// htmx POST handler for the "Add X missing to shopping list" button (P2-4b, J5 step 6).
    /// Idempotently SYNCS the recipe's slice to the current shortfall at <paramref name="servings"/>
    /// (plantry-gsj), then returns the re-rendered <c>_DetailsFulfilmentCard</c> partial swapping
    /// <c>#rd-fulf-card</c> — server-truth button labels plus a role=status summary of the reconciliation
    /// ("Added X · Y already on your list · Z checked off"). Returns 400 on invalid input, 403 on auth
    /// failure, 404 if the recipe was not found.
    /// </summary>
    public async Task<IActionResult> OnPostAddMissingAsync(int servings, CancellationToken ct)
    {
        var result = await addMissingService.ExecuteAsync(RecipeId.From(Id), servings, ct);
        return result switch
        {
            AddMissingResult.Added a          => await RenderCardAfterSyncAsync(servings, a.Outcome, ct),
            AddMissingResult.NothingMissing   => await RenderCardAfterSyncAsync(servings, ShoppingSyncOutcome.None, ct),
            AddMissingResult.NotFound         => NotFound(),
            AddMissingResult.Unauthorized     => Forbid(),
            AddMissingResult.Invalid          => BadRequest(),
            _                                 => StatusCode(500),
        };
    }

    /// <summary>
    /// htmx POST handler for the "Add all ingredients to shopping list" button (plantry-s1z).
    /// Idempotently SYNCS the recipe's slice to the full required set at <paramref name="servings"/>
    /// (plantry-gsj, SET/last-press-wins), then returns the re-rendered card partial as
    /// <see cref="OnPostAddMissingAsync"/> does. Returns 400 on invalid input, 403 on auth failure,
    /// 404 if the recipe was not found.
    /// </summary>
    public async Task<IActionResult> OnPostAddAllAsync(int servings, CancellationToken ct)
    {
        var result = await addAllService.ExecuteAsync(RecipeId.From(Id), servings, ct);
        return result switch
        {
            AddIngredientsResult.Added a        => await RenderCardAfterSyncAsync(servings, a.Outcome, ct),
            AddIngredientsResult.NothingToAdd   => await RenderCardAfterSyncAsync(servings, ShoppingSyncOutcome.None, ct),
            AddIngredientsResult.NotFound       => NotFound(),
            AddIngredientsResult.Unauthorized   => Forbid(),
            AddIngredientsResult.Invalid        => BadRequest(),
            _                                   => StatusCode(500),
        };
    }

    /// <summary>
    /// Re-renders the fulfilment card after a sync at the given servings, carrying the reconciliation
    /// <paramref name="outcome"/> so the aria-live summary line renders. Oob is false: only the primary
    /// <c>#rd-fulf-card</c> swaps — an add does not change per-ingredient stock statuses, so the OOB
    /// ingredient-rows block is not re-emitted.
    /// </summary>
    private async Task<IActionResult> RenderCardAfterSyncAsync(int servings, ShoppingSyncOutcome outcome, CancellationToken ct)
    {
        var recipe = await recipes.GetByIdAsync(RecipeId.From(Id), ct);
        if (recipe is null) return NotFound();

        var desiredServings = servings is > 0 and <= 24 ? servings : recipe.DefaultServings;
        var (productLookup, unitLookup, fulfillment) = await ResolveFulfilmentAsync(recipe, desiredServings, ct);

        var vm = await BuildCardModelAsync(
            recipe, desiredServings, productLookup, unitLookup, fulfillment, oob: false, summary: outcome, ct);

        return Partial("_DetailsFulfilmentCard", vm);
    }

    /// <summary>
    /// Parses the directions text into derived <see cref="DirectionBlock"/>s at render time (C13).
    /// Paragraphs (blank-line-separated blocks) become numbered Steps; lines starting with <c>#</c>
    /// are section headings that reset the step counter within the current section.
    /// The text is never mutated — this is pure view-time derivation.
    /// </summary>
    internal static IReadOnlyList<DirectionBlock> ParseDirections(string? directions)
    {
        if (string.IsNullOrWhiteSpace(directions)) return [];

        var blocks = new List<DirectionBlock>();
        var stepNumber = 0;
        var lines = directions.ReplaceLineEndings("\n").Split('\n');
        var paragraphLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0) return;
            var text = string.Join(" ", paragraphLines).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                stepNumber++;
                blocks.Add(new DirectionBlock(DirectionBlockKind.Step, Text: text, StepNumber: stepNumber));
            }
            paragraphLines.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                // Section heading — flush any in-progress paragraph, reset step counter for the new section.
                FlushParagraph();
                stepNumber = 0;
                var heading = line.TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(heading))
                    blocks.Add(new DirectionBlock(DirectionBlockKind.Section, Text: heading, StepNumber: 0));
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
            }
            else
            {
                paragraphLines.Add(line.Trim());
            }
        }

        FlushParagraph();
        return blocks;
    }
}

/// <summary>View model for the recipe Detail page.</summary>
public sealed record RecipeDetailView(
    Guid Id,
    string Name,
    int DefaultServings,
    int? CookTimeMinutes,
    string? Source,
    bool HasPhoto,
    IReadOnlyList<TagView> Tags,
    IReadOnlyList<IngredientGroupView> IngredientGroups,
    IReadOnlyList<DirectionBlock> DirectionBlocks,
    FulfillmentResult Fulfillment,
    CostPerServing Cost)
{
    internal static RecipeDetailView From(
        Recipe recipe,
        IReadOnlyDictionary<Guid, CatalogProductSummary> productLookup,
        IReadOnlyDictionary<Guid, string> unitLookup,
        IReadOnlyDictionary<TagId, string> tagLookup,
        IReadOnlyList<DirectionBlock> directions,
        FulfillmentResult fulfillment,
        CostPerServing cost)
    {
        // Group ingredients by GroupHeading with per-line fulfilment status (shared with the card partial).
        var groups = DetailsModel.BuildIngredientGroups(recipe, productLookup, unitLookup, fulfillment);

        var tagViews = recipe.Tags
            .Select(rt => new TagView(rt.TagId.Value, tagLookup.GetValueOrDefault(rt.TagId)))
            .ToList();

        return new RecipeDetailView(
            Id: recipe.Id.Value,
            Name: recipe.Name,
            DefaultServings: recipe.DefaultServings,
            CookTimeMinutes: recipe.CookTimeMinutes,
            Source: recipe.Source,
            HasPhoto: recipe.Photo is not null,
            Tags: tagViews,
            IngredientGroups: groups,
            DirectionBlocks: directions,
            Fulfillment: fulfillment,
            Cost: cost);
    }
}

/// <summary>A resolved tag pill for the detail view.</summary>
public sealed record TagView(Guid Id, string? Name);

/// <summary>A group of ingredients sharing the same optional section heading (C6).</summary>
public sealed record IngredientGroupView(
    string? Heading,
    IReadOnlyList<IngredientItemView> Items);

/// <summary>A single ingredient row with resolved product name, quantity, unit code, tracked state, and live fulfillment status.</summary>
public sealed record IngredientItemView(
    string ProductName,
    decimal? Quantity,
    string? UnitCode,
    bool IsUntracked,
    IngredientStatus Status,
    int? ExpiresWithinDays);

/// <summary>A derived direction block produced by <see cref="DetailsModel.ParseDirections"/> (C13).</summary>
public sealed record DirectionBlock(
    DirectionBlockKind Kind,
    string Text,
    int StepNumber);

public enum DirectionBlockKind { Step, Section }

/// <summary>
/// View model for the <c>_DetailsFulfilmentCard</c> partial (recipes-journeys.md J3).
/// Carries the fulfillment result recomputed at the requested servings, plus the full ingredient
/// group list (with updated per-line statuses) for the OOB ingredient-rows swap.
/// <para>
/// <see cref="Oob"/> controls whether <c>#rd-ing-rows</c> is emitted with
/// <c>hx-swap-oob="true"</c>. Set to <c>true</c> only in the htmx partial handler
/// (<see cref="DetailsModel.OnGetFulfilmentAsync"/>) so the OOB swap fires during a
/// fragment response. On the initial full-page render the flag is <c>false</c>, which
/// produces a clean inline block without the OOB attribute — htmx never processes
/// <c>hx-swap-oob</c> on elements already present in the initial DOM, so omitting it
/// prevents a visible duplicate ingredient list appearing in the rail.
/// Mirrors the <c>Model.Oob</c> pattern used by <c>_PantrySuggestions.cshtml</c> and
/// <c>_ShoppingSummary.cshtml</c>.
/// </para>
/// </summary>
public sealed record DetailsFulfilmentCardModel(
    Guid RecipeId,
    int DefaultServings,
    FulfillmentResult Fulfillment,
    IReadOnlyList<IngredientGroupView> IngredientGroups,
    bool Oob = false,
    AddButtonView? MissingButton = null,
    AddButtonView? AllButton = null,
    ShoppingSyncOutcome? Summary = null);

/// <summary>
/// Render state for one recipe "add to shopping list" button (plantry-gsj): whether to show it at all,
/// its three-way <see cref="AddButtonState"/>, and the N in "Add N missing" / "Add N more".
/// </summary>
public sealed record AddButtonView(bool Show, AddButtonState State, int PendingLines);
