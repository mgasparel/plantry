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
    CostingService costingService) : PageModel
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
