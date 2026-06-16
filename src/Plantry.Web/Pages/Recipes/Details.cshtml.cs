using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

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
    AddMissingToShoppingList addMissingService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public RecipeDetailView Recipe { get; private set; } = null!;

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

        var productIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
        var productLookup = await catalog.ResolveSummariesAsync(productIds, ct);

        var unitIds = recipe.Ingredients
            .Where(i => i.UnitId is not null)
            .Select(i => i.UnitId!.Value)
            .Distinct()
            .ToList();
        var unitLookup = await catalog.ResolveUnitCodesAsync(unitIds, ct);

        // Recompute fulfillment at the requested servings (not hardcoded to DefaultServings).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeAsync(recipe, desiredServings, today, ct);

        // Build a fulfillment-keyed ingredient group list so the partial can render
        // per-ingredient status dots updated to the new serving count.
        var fulfillmentByIngredientId = fulfillment.Lines.ToDictionary(l => l.IngredientId, l => l);
        var ingredientGroups = recipe.Ingredients
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

        // Oob: true so the partial's #rd-ing-rows block carries hx-swap-oob="true" and htmx
        // replaces the ingredient rows in place alongside the primary #rd-fulf-card swap.
        var vm = new DetailsFulfilmentCardModel(
            RecipeId: recipe.Id.Value,
            DefaultServings: recipe.DefaultServings,
            Fulfillment: fulfillment,
            IngredientGroups: ingredientGroups,
            Oob: true);

        return Partial("_DetailsFulfilmentCard", vm);
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
    /// Calls <see cref="AddMissingToShoppingList"/> at <paramref name="servings"/>; on success
    /// returns 200 OK (no body — the Alpine state flip is handled client-side via hx-on::after-request).
    /// Returns 400 on invalid input, 403 on auth failure, 404 if the recipe was not found.
    /// </summary>
    public async Task<IActionResult> OnPostAddMissingAsync(int servings, CancellationToken ct)
    {
        var result = await addMissingService.ExecuteAsync(RecipeId.From(Id), servings, ct);
        return result switch
        {
            AddMissingResult.Added          => new OkResult(),
            AddMissingResult.NothingMissing => new OkResult(),  // idempotent — button tapped with 0 missing
            AddMissingResult.NotFound       => NotFound(),
            AddMissingResult.Unauthorized   => Forbid(),
            AddMissingResult.Invalid        => BadRequest(),
            _                               => StatusCode(500),
        };
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
        // Index fulfillment lines by IngredientId for O(1) lookup during ingredient group building.
        var fulfillmentByIngredientId = fulfillment.Lines
            .ToDictionary(l => l.IngredientId, l => l);

        // Group ingredients by GroupHeading (null heading = ungrouped), preserving ordinal order.
        var groups = recipe.Ingredients
            .OrderBy(i => i.Ordinal)
            .GroupBy(i => i.GroupHeading)
            .Select(g => new IngredientGroupView(
                Heading: g.Key,
                Items: g.Select(i =>
                {
                    productLookup.TryGetValue(i.ProductId, out var product);
                    // Untracked staples are shown without qty/unit (C12 — "to taste" etc.).
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
    bool Oob = false);
