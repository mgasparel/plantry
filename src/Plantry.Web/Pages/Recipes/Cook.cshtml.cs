using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

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
    /// Per-line quantity overrides from the Cook page quantity stepper (C9 modify).
    /// Key = IngredientId, Value = user-entered quantity. Only present when the user
    /// changed a quantity from its scaled default. Posted as
    /// <c>QuantityOverrides[{guid}]={value}</c> hidden inputs emitted by Alpine.
    /// </summary>
    [BindProperty]
    public Dictionary<Guid, decimal> QuantityOverrides { get; set; } = [];

    /// <summary>
    /// Existing catalog products the user added to THIS cook via the Add-product search picker
    /// (plantry-7zjm). Each is consumed as part of the cook with no source recipe ingredient (the
    /// recipe definition is untouched). Posted as <c>AddedLines[N].ProductId</c> /
    /// <c>AddedLines[N].Quantity</c> / <c>AddedLines[N].UnitId</c> hidden inputs emitted by Alpine.
    /// Search-only — there is no create-new-product path from the Cook page.
    /// </summary>
    [BindProperty]
    public List<AddedLineInput> AddedLines { get; set; } = [];

    /// <summary>
    /// Unit options for the added-product rows' unit selector (plantry-7zjm). Loaded on GET via the
    /// Catalog anti-corruption port; the Cook page never queries Catalog repositories directly (Gate 2).
    /// </summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>
    /// Returns <c>&lt;li role="option"&gt;</c> markup for the Add-product searchable-select
    /// (plantry-7zjm). Called by htmx on keyup in the Cook-page product search field. Search-only:
    /// each option dispatches <c>pick-product</c> with the product's id, name, and default unit so the
    /// Cook page can append an editable added-product row. Mirrors the Recipes/Edit ingredient search
    /// handler; the <c>data-track</c> flag lets the page skip untracked products (there is no stock to
    /// consume). No "create new product" affordance is emitted — the picker is deliberately search-only.
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var hits = await catalog.SearchAsync(q.Trim(), ct);
        var enc = HtmlEncoder.Default;

        var defaultUnitIds = hits.Select(p => p.DefaultUnitId).Distinct().ToList();
        var unitCodes = defaultUnitIds.Count > 0
            ? await catalog.ResolveUnitCodesAsync(defaultUnitIds, ct)
            : (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();

        var html = string.Join("", hits.Select((p, i) =>
        {
            var label = ProductNameMatcher.RankLabel(p.Score, isTopHit: i == 0);
            var unitCode = unitCodes.GetValueOrDefault(p.DefaultUnitId, "");
            return $$"""<li role="option" data-value="{{p.Id}}" data-name="{{enc.Encode(p.Name)}}" data-track="{{(p.TrackStock ? "true" : "false")}}" data-default-unit="{{p.DefaultUnitId}}" data-default-unit-code="{{enc.Encode(unitCode)}}" @click="query = $el.dataset.name; open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.dataset.name, track: $el.dataset.track, defaultUnit: $el.dataset.defaultUnit, defaultUnitCode: $el.dataset.defaultUnitCode})">{{enc.Encode(p.Name)}}<span class="rk">{{enc.Encode(label)}}</span></li>""";
        }));
        return Content(html, "text/html");
    }

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

        // Unit codes for the stock default units — needed to render the real on-hand amount in the
        // stock's own unit when a recipe unit can't be converted to it (unit-gap display, plantry-qll2.5).
        var stockUnitIds = stockById.Values.Select(s => s.DefaultUnitId).Distinct().ToList();
        var stockUnitCodes = stockUnitIds.Count > 0
            ? await catalog.ResolveUnitCodesAsync(stockUnitIds, ct)
            : new Dictionary<Guid, string>();

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
                var isUnitGap = false;
                decimal? stockQuantity = null;
                string? stockUnitCode = null;

                if (stock is not null && ingredient.UnitId.HasValue
                    && stock.DefaultUnitId != ingredient.UnitId.Value)
                {
                    var conv = await unitConverter.ConvertAsync(
                        ingredient.ProductId, availableRaw, stock.DefaultUnitId, ingredient.UnitId.Value, ct);
                    if (conv.IsSuccess)
                    {
                        availableInIngUnit = conv.Value;
                    }
                    else
                    {
                        // No conversion path between the stock unit and the recipe unit. When there is
                        // real stock on hand this is a DISTINCT state from an empty pantry
                        // (plantry-qll2.5): the user may be holding a full bag we simply cannot compare.
                        // Surface the honest on-hand amount in the stock unit rather than collapsing to a
                        // "have 0 / need N" shortfall. The consume-path mirrors this: after plantry-qll2.6
                        // a unit-gap POST lands the cook consume line as DeferredUnitGap — the full
                        // requested quantity is recorded as owed and retro-applied once a conversion
                        // bridges the unit pair — NOT Shorted (which would be a genuine, never-retried
                        // no-stock outcome).
                        availableInIngUnit = 0m;
                        if (availableRaw > 0m)
                        {
                            isUnitGap = true;
                            stockQuantity = availableRaw;
                            stockUnitCode = stockUnitCodes.GetValueOrDefault(stock.DefaultUnitId);
                        }
                    }
                }

                // A unit gap is not a shortfall — we cannot make the comparison at all, so we do not
                // claim the pantry is short.
                var isShortfall = !isUnitGap && scaledQty.HasValue && availableInIngUnit < scaledQty.Value;

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
                    IsShortfall: isShortfall,
                    IsUnitGap: isUnitGap,
                    StockQuantity: stockQuantity,
                    StockUnitCode: stockUnitCode));
            }
        }

        // Unit options for the Add-product rows (plantry-7zjm) — via the anti-corruption port (Gate 2).
        var unitOptions = await catalog.ListUnitsAsync(ct);
        UnitOptions = unitOptions
            .Select(u => new SelectListItem(u.Code, u.Id.ToString()))
            .ToList();

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

        // Ad-hoc added products (plantry-7zjm): existing catalog products the user added to this cook.
        // Filter malformed rows (no product, no unit, or a non-positive quantity) so a blank/partial
        // row never reaches the service. The server re-validates TrackStock in CookRecipe (C12).
        var adHocLines = AddedLines
            .Where(a => a.ProductId != Guid.Empty && a.UnitId != Guid.Empty && a.Quantity > 0m)
            .Select(a => new AdHocLine(a.ProductId, a.Quantity, a.UnitId))
            .ToList();

        var command = new CookRecipeCommand(
            RecipeId: id,
            DesiredServings: desiredServings,
            UserId: Guid.Parse(userId),
            Resolutions: resolutions,
            AdHocLines: adHocLines);

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
/// <param name="IsUnitGap">
/// True when the product has real stock on hand but its stock unit cannot be converted to the recipe
/// unit (plantry-qll2.5). Mutually exclusive with <paramref name="IsShortfall"/> — a unit gap is an
/// honest "can't compare" state, not an empty pantry. Rendered in info tone with the real on-hand
/// amount and an "Add conversion" link, never as a "have 0 / need N" warning.
/// </param>
/// <param name="StockQuantity">The real on-hand quantity in the stock's own unit — set only when
/// <paramref name="IsUnitGap"/> is true.</param>
/// <param name="StockUnitCode">The stock unit's display code (e.g. "g") — set only when
/// <paramref name="IsUnitGap"/> is true.</param>
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
    bool IsShortfall,
    bool IsUnitGap = false,
    decimal? StockQuantity = null,
    string? StockUnitCode = null);

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

/// <summary>
/// Form input model for one added product on the Cook page (plantry-7zjm). Bound from the
/// <c>AddedLines[N].*</c> hidden inputs the Alpine <c>x-for</c> emits per added row. Search-only —
/// <see cref="ProductId"/> always references an existing catalog product.
/// </summary>
public sealed record AddedLineInput
{
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public Guid UnitId { get; set; }
}
