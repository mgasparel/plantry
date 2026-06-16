using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// J4 Cook confirmation page (recipes-journeys.md J4). Entered from the Detail page "Cook this"
/// button at the displayed servings count.
///
/// GET: Builds the <see cref="CookViewModel"/> — scaled ingredient quantities, variant choices for
/// parent-product ingredients (C7/C11), and live stock availability for the shortfall-but-proceed
/// affordance (C8/R9).
///
/// POST: Accepts the caller's <see cref="IngredientResolution"/>[] (the Variant Disambiguation
/// Picker output) and executes <see cref="CookRecipe"/>. On success redirects back to the Detail
/// page (P2-2d) so fulfillment/cost are RE-COMPUTED to reflect the pantry decrement.
/// </summary>
[Authorize]
public sealed class CookModel(
    IRecipeRepository recipes,
    ICatalogProductReader catalog,
    IInventoryStockReader stockReader,
    IUnitConverter unitConverter,
    CookRecipe cookService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    /// <summary>
    /// Desired servings — passed as a query parameter from the Detail page stepper.
    /// Defaults to the recipe's <see cref="Recipe.DefaultServings"/> when absent.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public int? Servings { get; set; }

    public CookViewModel Cook { get; private set; } = null!;

    // ── Resolution inputs (bound from POST form fields) ──────────────────────────────────────────

    /// <summary>
    /// The IngredientIds for ingredients the user has chosen to skip (C9).
    /// Posted as repeated hidden inputs: <c>SkippedIngredientIds[]=…</c>.
    /// </summary>
    [BindProperty]
    public List<Guid> SkippedIngredientIds { get; set; } = [];

    /// <summary>
    /// Variant picker selections: ingredientId → chosen variantProductId (C7/C11).
    /// Each entry is a single variant selection; absent entries use default auto-selection.
    /// Posted as <c>PickerSelections[N].IngredientId</c> / <c>PickerSelections[N].VariantId</c>.
    /// </summary>
    [BindProperty]
    public List<PickerSelectionInput> PickerSelections { get; set; } = [];

    /// <summary>
    /// Per-line quantity overrides from the Cook page qty-stepper (C9 modify).
    /// Key = IngredientId, Value = user-entered quantity. Only present when the user
    /// changed a quantity from its scaled default. Posted as
    /// <c>QuantityOverrides[{guid}]={value}</c> hidden inputs emitted by Alpine.
    /// </summary>
    [BindProperty]
    public Dictionary<Guid, decimal> QuantityOverrides { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return NotFound();

        var desiredServings = Servings is > 0 ? Servings.Value : recipe.DefaultServings;
        var scale = (decimal)desiredServings / recipe.DefaultServings;

        // Resolve catalog facts for all ingredients (track_stock + parent/variant tree).
        var allProductIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
        var catalogById = new Dictionary<Guid, CatalogProduct>();
        foreach (var productId in allProductIds)
        {
            var product = await catalog.FindAsync(productId, ct);
            if (product is not null)
                catalogById[product.Id] = product;
        }

        // Resolve unit codes for rendering.
        var unitIds = recipe.Ingredients
            .Where(i => i.UnitId is not null)
            .Select(i => i.UnitId!.Value)
            .Distinct()
            .ToList();
        var unitCodes = await catalog.ResolveUnitCodesAsync(unitIds, ct);

        // Resolve variant product names and unit codes.
        var variantIds = catalogById.Values
            .Where(p => p.IsParent)
            .SelectMany(p => p.VariantProductIds)
            .Distinct()
            .ToList();
        var variantSummaries = variantIds.Count > 0
            ? await catalog.ResolveSummariesAsync(variantIds, ct)
            : new Dictionary<Guid, CatalogProductSummary>();
        var variantDefaultUnits = new Dictionary<Guid, Guid>();
        foreach (var variantId in variantIds)
        {
            var variantProduct = await catalog.FindAsync(variantId, ct);
            if (variantProduct is not null)
                variantDefaultUnits[variantId] = variantProduct.DefaultUnitId;
        }

        // Resolve stock for all relevant products (variants for parents, direct for leaves).
        var stockIds = new HashSet<Guid>();
        foreach (var (productId, catalogProduct) in catalogById)
        {
            if (!catalogProduct.TrackStock) continue;
            if (catalogProduct.IsParent)
                foreach (var v in catalogProduct.VariantProductIds)
                    stockIds.Add(v);
            else
                stockIds.Add(productId);
        }
        var stockById = stockIds.Count > 0
            ? await stockReader.FindStockBatchAsync(stockIds.ToList(), ct)
            : new Dictionary<Guid, ProductStock>();

        // Unit codes for variant products.
        var variantUnitIds = variantDefaultUnits.Values.Distinct().ToList();
        var variantUnitCodes = variantUnitIds.Count > 0
            ? await catalog.ResolveUnitCodesAsync(variantUnitIds, ct)
            : new Dictionary<Guid, string>();

        // Build the view model lines.
        var lines = new List<CookLineView>();
        foreach (var ingredient in recipe.Ingredients.OrderBy(i => i.Ordinal))
        {
            if (!catalogById.TryGetValue(ingredient.ProductId, out var product))
                continue; // unknown product — skip

            var scaledQty = ingredient.Quantity.HasValue ? ingredient.Quantity.Value * scale : (decimal?)null;
            var unitCode = ingredient.UnitId.HasValue ? unitCodes.GetValueOrDefault(ingredient.UnitId.Value) : null;

            if (!product.TrackStock)
            {
                // Untracked staple (C12): show but no quantity / picker.
                lines.Add(new CookLineView(
                    IngredientId: ingredient.Id.Value,
                    ProductId: ingredient.ProductId,
                    ProductName: product.Name,
                    GroupHeading: ingredient.GroupHeading,
                    ScaledQuantity: null,
                    UnitCode: null,
                    IsUntracked: true,
                    IsParent: false,
                    VariantOptions: [],
                    AvailableQuantity: null,
                    IsShortfall: false));
                continue;
            }

            if (product.IsParent && ingredient.Quantity.HasValue && ingredient.UnitId.HasValue)
            {
                // Parent product: build variant options with stock and auto-selection (C7/C11).
                var options = new List<VariantOptionView>();
                Guid? bestVariantId = null;
                decimal bestAvailable = -1m;

                foreach (var variantId in product.VariantProductIds)
                {
                    variantSummaries.TryGetValue(variantId, out var variantSummary);
                    stockById.TryGetValue(variantId, out var variantStock);

                    var variantName = variantSummary?.Name ?? "(unknown)";
                    var variantAvailable = variantStock?.AvailableQuantity ?? 0m;

                    // Check unit compatibility: can we convert variant's default unit to ingredient unit?
                    var isCompatible = true;
                    if (variantDefaultUnits.TryGetValue(variantId, out var variantDefaultUnit))
                    {
                        if (variantDefaultUnit != ingredient.UnitId.Value)
                        {
                            var testConvert = await unitConverter.ConvertAsync(
                                variantId, 1m, variantDefaultUnit, ingredient.UnitId.Value, ct);
                            isCompatible = testConvert.IsSuccess;
                        }
                    }

                    var variantAvailableInIngUnit = 0m;
                    if (isCompatible && variantStock is not null &&
                        variantDefaultUnits.TryGetValue(variantId, out var defUnit))
                    {
                        var conv = await unitConverter.ConvertAsync(
                            variantId, variantAvailable, defUnit, ingredient.UnitId.Value, ct);
                        if (conv.IsSuccess) variantAvailableInIngUnit = conv.Value;
                    }

                    var variantUnitCode = variantDefaultUnits.TryGetValue(variantId, out var vUnitId)
                        ? variantUnitCodes.GetValueOrDefault(vUnitId)
                        : unitCode;

                    // FEFO best-selection: prefer in-stock (available >= required), then by highest available.
                    if (isCompatible && variantAvailableInIngUnit > bestAvailable)
                    {
                        bestAvailable = variantAvailableInIngUnit;
                        bestVariantId = variantId;
                    }

                    options.Add(new VariantOptionView(
                        VariantId: variantId,
                        VariantName: variantName,
                        AvailableQuantity: variantAvailableInIngUnit,
                        UnitCode: variantUnitCode ?? unitCode,
                        IsCompatible: isCompatible,
                        IsAutoSelected: false)); // filled in below
                }

                // Mark best as auto-selected.
                options = options.Select(o => o with { IsAutoSelected = o.VariantId == bestVariantId }).ToList();

                // Total available for shortfall computation (sum across compatible variants in ingredient unit).
                var totalAvailable = options
                    .Where(o => o.IsCompatible)
                    .Sum(o => o.AvailableQuantity);
                var isShortfall = scaledQty.HasValue && totalAvailable < scaledQty.Value;

                lines.Add(new CookLineView(
                    IngredientId: ingredient.Id.Value,
                    ProductId: ingredient.ProductId,
                    ProductName: product.Name,
                    GroupHeading: ingredient.GroupHeading,
                    ScaledQuantity: scaledQty,
                    UnitCode: unitCode,
                    IsUntracked: false,
                    IsParent: true,
                    VariantOptions: options,
                    AvailableQuantity: totalAvailable,
                    IsShortfall: isShortfall));
            }
            else if (!product.IsParent)
            {
                // Leaf product.
                stockById.TryGetValue(ingredient.ProductId, out var stock);
                var availableRaw = stock?.AvailableQuantity ?? 0m;
                var availableInIngUnit = availableRaw;

                if (stock is not null && ingredient.UnitId.HasValue
                    && stock.DefaultUnitId != ingredient.UnitId.Value)
                {
                    var conv = await unitConverter.ConvertAsync(
                        ingredient.ProductId, availableRaw, stock.DefaultUnitId, ingredient.UnitId.Value, ct);
                    if (conv.IsSuccess) availableInIngUnit = conv.Value;
                    else availableInIngUnit = 0m;
                }

                var isShortfall = scaledQty.HasValue && availableInIngUnit < scaledQty.Value;

                lines.Add(new CookLineView(
                    IngredientId: ingredient.Id.Value,
                    ProductId: ingredient.ProductId,
                    ProductName: product.Name,
                    GroupHeading: ingredient.GroupHeading,
                    ScaledQuantity: scaledQty,
                    UnitCode: unitCode,
                    IsUntracked: false,
                    IsParent: false,
                    VariantOptions: [],
                    AvailableQuantity: availableInIngUnit,
                    IsShortfall: isShortfall));
            }
        }

        Cook = new CookViewModel(
            RecipeId: recipe.Id.Value,
            RecipeName: recipe.Name,
            DesiredServings: desiredServings,
            DefaultServings: recipe.DefaultServings,
            Scale: scale,
            Lines: lines);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var desiredServings = Servings is > 0 ? Servings.Value : 1;

        // Resolve the current user id from the HttpContext.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Forbid();

        // Build IngredientResolution[] from form inputs.
        var skippedSet = SkippedIngredientIds.ToHashSet();
        var pickerIndex = PickerSelections
            .Where(p => p.VariantId != Guid.Empty)
            .ToDictionary(p => p.IngredientId, p => p.VariantId);

        // We need the recipe to resolve ingredient ids and their unit ids for allocation quantities.
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return NotFound();

        var scale = (decimal)desiredServings / recipe.DefaultServings;

        var resolutions = new List<IngredientResolution>();
        foreach (var ingredient in recipe.Ingredients)
        {
            var ingId = ingredient.Id;
            if (skippedSet.Contains(ingId.Value))
            {
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: true, Allocations: []));
                continue;
            }

            var scaledQty = ingredient.Quantity.HasValue ? ingredient.Quantity.Value * scale : 0m;
            var hasOverride = QuantityOverrides.TryGetValue(ingId.Value, out var ovr) && ovr >= 0m;
            var effectiveQty = hasOverride ? ovr : scaledQty;

            if (pickerIndex.TryGetValue(ingId.Value, out var chosenVariantId))
            {
                // User selected a specific variant; allocate the effective quantity to it.
                var unitId = ingredient.UnitId ?? Guid.Empty;
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: false, Allocations:
                [
                    new VariantAllocation(chosenVariantId, effectiveQty, unitId),
                ]));
            }
            else if (hasOverride)
            {
                // Leaf ingredient with a quantity override: emit an explicit resolution so
                // CookRecipe uses the user-entered quantity rather than the scaled default.
                var unitId = ingredient.UnitId ?? Guid.Empty;
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: false, Allocations:
                [
                    new VariantAllocation(ingredient.ProductId, effectiveQty, unitId),
                ]));
            }
            // No entry for this ingredient → default auto-selection in CookRecipe service.
        }

        var command = new CookRecipeCommand(
            RecipeId: id,
            DesiredServings: desiredServings,
            UserId: Guid.Parse(userId),
            Resolutions: resolutions);

        var result = await cookService.ExecuteAsync(command, ct);

        return result switch
        {
            CookRecipeResult.Cooked => RedirectToPage("/Recipes/Details", new { id = Id }),
            CookRecipeResult.Invalid inv => BadRequest(inv.Error.Description),
            _ => BadRequest("Unknown cook result."),
        };
    }
}

// ── View models ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level view model for the Cook confirmation page.</summary>
public sealed record CookViewModel(
    Guid RecipeId,
    string RecipeName,
    int DesiredServings,
    int DefaultServings,
    decimal Scale,
    IReadOnlyList<CookLineView> Lines);

/// <summary>One ingredient line on the Cook confirmation page.</summary>
public sealed record CookLineView(
    Guid IngredientId,
    Guid ProductId,
    string ProductName,
    string? GroupHeading,
    decimal? ScaledQuantity,
    string? UnitCode,
    bool IsUntracked,
    bool IsParent,
    IReadOnlyList<VariantOptionView> VariantOptions,
    decimal? AvailableQuantity,
    bool IsShortfall);

/// <summary>One variant option within a parent-product ingredient's Variant Disambiguation Picker (C7/C11).</summary>
public sealed record VariantOptionView(
    Guid VariantId,
    string VariantName,
    decimal AvailableQuantity,
    string? UnitCode,
    bool IsCompatible,
    bool IsAutoSelected);

/// <summary>Form input model for the variant picker selection (one per parent-product ingredient).</summary>
public sealed record PickerSelectionInput
{
    public Guid IngredientId { get; set; }
    public Guid VariantId { get; set; }
}
