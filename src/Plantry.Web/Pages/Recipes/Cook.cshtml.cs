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
/// affordance (C8/R9). With recipe composition (recipe-composition.md §6, D6/D7) the page reads the
/// EXPANDED line list (<see cref="RecipeExpansionService"/>): the recipe's own direct ingredients
/// render in the main card exactly as before, and every inclusion becomes its own group (sub name +
/// effective servings) carrying its expanded lines with the same per-line toolkit plus a whole-inclusion
/// skip on the header.
///
/// POST: Accepts the caller's <see cref="IngredientResolution"/>[] (the Variant Disambiguation
/// Picker output), keyed by the PATH-QUALIFIED line identity (direct lines use an empty path so the
/// pre-composition form contract maps 1:1), and executes <see cref="CookRecipe"/>. On success redirects
/// back to the Detail page (P2-2d) so fulfillment/cost are RE-COMPUTED to reflect the pantry decrement.
/// </summary>
[Authorize]
public sealed class CookModel(
    IRecipeRepository recipes,
    ICatalogProductReader catalog,
    IInventoryStockReader stockReader,
    IUnitConverter unitConverter,
    RecipeExpansionService expansion,
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
    /// The IngredientIds for direct ingredients the user has chosen to skip (C9).
    /// Posted as repeated hidden inputs: <c>SkippedIngredientIds[]=…</c>.
    /// </summary>
    [BindProperty]
    public List<Guid> SkippedIngredientIds { get; set; } = [];

    /// <summary>
    /// Variant picker selections for direct ingredients: ingredientId → chosen variantProductId (C7/C11).
    /// Each entry is a single variant selection; absent entries use default auto-selection.
    /// Posted as <c>PickerSelections[N].IngredientId</c> / <c>PickerSelections[N].VariantId</c>.
    /// </summary>
    [BindProperty]
    public List<PickerSelectionInput> PickerSelections { get; set; } = [];

    /// <summary>
    /// Per-line quantity overrides for direct ingredients from the Cook page quantity stepper (C9 modify).
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

    // ── Inclusion resolution inputs (recipe-composition.md §6, D6/D7) ─────────────────────────────
    // These carry the PATH-QUALIFIED resolutions for lines pulled in through an inclusion; direct
    // lines keep the flat fields above (empty path — byte-for-byte the pre-composition contract).
    // The lineKey = "{PathKey}|{IngredientId}" where PathKey is the '/'-joined InclusionId chain
    // (matches ExpandedLine.PathKey / IngredientResolution.PathKey).

    /// <summary>
    /// Whole-inclusion skips (D7): the '/'-joined <c>InclusionId</c> path of each inclusion the user
    /// skipped in one action. Each drops every expanded line beneath that path prefix. Posted as
    /// repeated <c>SkippedInclusions</c> hidden inputs.
    /// </summary>
    [BindProperty]
    public List<string> SkippedInclusions { get; set; } = [];

    /// <summary>
    /// Per-line skips (C9) on expanded inclusion lines, each identified by its lineKey. Posted as
    /// repeated <c>SkippedInclusionLineKeys</c> hidden inputs.
    /// </summary>
    [BindProperty]
    public List<string> SkippedInclusionLineKeys { get; set; } = [];

    /// <summary>
    /// Variant Disambiguation Picker selections (C7/C11) on expanded inclusion lines: lineKey →
    /// chosen variant product id. Posted as <c>InclusionPickerSelections[{lineKey}]={variantId}</c>.
    /// </summary>
    [BindProperty]
    public Dictionary<string, Guid> InclusionPickerSelections { get; set; } = [];

    /// <summary>
    /// Per-line quantity overrides (C9 modify) on expanded inclusion lines: lineKey → user quantity.
    /// Posted as <c>InclusionQuantityOverrides[{lineKey}]={value}</c>.
    /// </summary>
    [BindProperty]
    public Dictionary<string, decimal> InclusionQuantityOverrides { get; set; } = [];

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

        // ── Expand to flat product-level lines (D4 choke point, recipe-composition.md §6) ──────────
        // A flat recipe expands to its own direct ingredients (empty path); a recipe with inclusions
        // also yields each sub's ingredients pre-scaled by the expansion factor. On a defensive
        // expansion failure (missing sub / in-memory cycle — N4 prevents the latter at save) fall back
        // to the recipe's own direct ingredients so the page still renders rather than 500ing.
        var expandResult = await expansion.ExpandAsync(recipe.Id, ct);
        var expandedLines = expandResult.IsSuccess
            ? expandResult.Value
            : recipe.Ingredients
                .OrderBy(i => i.Ordinal)
                .Select(i => new ExpandedLine(
                    [], i.Id, recipe.Id, i.ProductId, i.Quantity, i.UnitId,
                    i.GroupHeading is null ? [] : [i.GroupHeading]))
                .ToList();

        // Resolve catalog facts for every expanded product (track_stock + parent/variant tree).
        var allProductIds = expandedLines.Select(e => e.ProductId).Distinct().ToList();
        var catalogById = new Dictionary<Guid, CatalogProduct>();
        foreach (var productId in allProductIds)
        {
            var product = await catalog.FindAsync(productId, ct);
            if (product is not null)
                catalogById[product.Id] = product;
        }

        // Resolve unit codes for rendering.
        var unitIds = expandedLines
            .Where(e => e.UnitId is not null)
            .Select(e => e.UnitId!.Value)
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

        var renderContext = new CookRenderContext(
            catalogById, unitCodes, variantSummaries, variantDefaultUnits,
            stockById, stockUnitCodes, variantUnitCodes);

        // Inclusion group metadata (sub name + effective servings) via a household-scoped walk of the
        // inclusion tree (recipe-composition.md §6, D2): effective servings = Inclusion.Servings × the
        // accumulated batch factor along the path × ServingsScale.
        var groupMeta = await BuildInclusionGroupMetaAsync(recipe, desiredServings, ct);

        // Build line views. Direct lines (empty path) render in the main card exactly as pre-composition;
        // inclusion lines are grouped under their path, in author (first-appearance) order.
        var directLines = new List<CookLineView>();
        var groupLines = new Dictionary<string, List<CookLineView>>();
        var groupOrder = new List<string>();
        foreach (var line in expandedLines)
        {
            // Direct ingredients carry their own GroupHeading (the deepest GroupPath segment when the
            // path is empty); inclusion lines render flat under the inclusion header (no sub-group heading).
            var groupHeading = line.Path.Count == 0 && line.GroupPath.Count > 0
                ? line.GroupPath[^1]
                : null;

            var view = await BuildLineViewAsync(line, groupHeading, scale, renderContext, ct);
            if (view is null) continue; // unknown product — skip

            if (line.Path.Count == 0)
            {
                directLines.Add(view);
            }
            else
            {
                if (!groupLines.TryGetValue(line.PathKey, out var list))
                {
                    list = [];
                    groupLines[line.PathKey] = list;
                    groupOrder.Add(line.PathKey);
                }
                list.Add(view);
            }
        }

        var inclusionGroups = groupOrder
            .Select(pathKey =>
            {
                groupMeta.TryGetValue(pathKey, out var meta);
                return new CookInclusionGroupView(
                    PathKey: pathKey,
                    SubRecipeName: meta?.SubName ?? "Included recipe",
                    EffectiveServings: meta?.EffectiveServings ?? 0m,
                    Lines: groupLines[pathKey]);
            })
            .ToList();

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
            Lines: directLines,
            InclusionGroups: inclusionGroups);

        return Page();
    }

    /// <summary>
    /// Builds one <see cref="CookLineView"/> from an <see cref="ExpandedLine"/> — the shared per-line
    /// resolver used for both the recipe's own direct ingredients (empty path) and every expanded
    /// inclusion line (path-qualified). Direct lines carry <c>LineKey == IngredientId</c> so the
    /// pre-composition Alpine keying and form contract are preserved byte-for-byte. Returns <c>null</c>
    /// when the line's product is unknown in the catalog (skipped from render).
    /// </summary>
    private async Task<CookLineView?> BuildLineViewAsync(
        ExpandedLine line, string? groupHeading, decimal scale, CookRenderContext ctx, CancellationToken ct)
    {
        if (!ctx.CatalogById.TryGetValue(line.ProductId, out var product))
            return null; // unknown product — skip

        var pathKey = line.PathKey;
        var isInclusion = pathKey.Length != 0;
        var ingredientId = line.IngredientId.Value;
        var lineKey = isInclusion ? $"{pathKey}|{ingredientId}" : ingredientId.ToString();

        var scaledQty = line.Quantity.HasValue ? line.Quantity.Value * scale : (decimal?)null;
        var unitCode = line.UnitId.HasValue ? ctx.UnitCodes.GetValueOrDefault(line.UnitId.Value) : null;

        if (!product.TrackStock)
        {
            // Untracked staple (C12): show but no quantity / picker.
            return new CookLineView(
                IngredientId: ingredientId,
                LineKey: lineKey,
                PathKey: pathKey,
                IsInclusion: isInclusion,
                ProductId: line.ProductId,
                ProductName: product.Name,
                GroupHeading: groupHeading,
                ScaledQuantity: null,
                UnitCode: null,
                IsUntracked: true,
                IsParent: false,
                VariantOptions: [],
                AvailableQuantity: null,
                IsShortfall: false);
        }

        if (product.IsParent && line.Quantity.HasValue && line.UnitId.HasValue)
        {
            // Parent product: build variant options with stock and auto-selection (C7/C11).
            var options = new List<VariantOptionView>();
            Guid? bestVariantId = null;
            decimal bestAvailable = -1m;

            foreach (var variantId in product.VariantProductIds)
            {
                ctx.VariantSummaries.TryGetValue(variantId, out var variantSummary);
                ctx.StockById.TryGetValue(variantId, out var variantStock);

                var variantName = variantSummary?.Name ?? "(unknown)";
                var variantAvailable = variantStock?.AvailableQuantity ?? 0m;

                // Check unit compatibility: can we convert variant's default unit to ingredient unit?
                var isCompatible = true;
                if (ctx.VariantDefaultUnits.TryGetValue(variantId, out var variantDefaultUnit))
                {
                    if (variantDefaultUnit != line.UnitId.Value)
                    {
                        var testConvert = await unitConverter.ConvertAsync(
                            variantId, 1m, variantDefaultUnit, line.UnitId.Value, ct);
                        isCompatible = testConvert.IsSuccess;
                    }
                }

                var variantAvailableInIngUnit = 0m;
                if (isCompatible && variantStock is not null &&
                    ctx.VariantDefaultUnits.TryGetValue(variantId, out var defUnit))
                {
                    var conv = await unitConverter.ConvertAsync(
                        variantId, variantAvailable, defUnit, line.UnitId.Value, ct);
                    if (conv.IsSuccess) variantAvailableInIngUnit = conv.Value;
                }

                var variantUnitCode = ctx.VariantDefaultUnits.TryGetValue(variantId, out var vUnitId)
                    ? ctx.VariantUnitCodes.GetValueOrDefault(vUnitId)
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

            return new CookLineView(
                IngredientId: ingredientId,
                LineKey: lineKey,
                PathKey: pathKey,
                IsInclusion: isInclusion,
                ProductId: line.ProductId,
                ProductName: product.Name,
                GroupHeading: groupHeading,
                ScaledQuantity: scaledQty,
                UnitCode: unitCode,
                IsUntracked: false,
                IsParent: true,
                VariantOptions: options,
                AvailableQuantity: totalAvailable,
                IsShortfall: isShortfall);
        }

        // Leaf product.
        {
            ctx.StockById.TryGetValue(line.ProductId, out var stock);
            var availableRaw = stock?.AvailableQuantity ?? 0m;
            var availableInIngUnit = availableRaw;
            var isUnitGap = false;
            decimal? stockQuantity = null;
            string? stockUnitCode = null;

            if (stock is not null && line.UnitId.HasValue
                && stock.DefaultUnitId != line.UnitId.Value)
            {
                var conv = await unitConverter.ConvertAsync(
                    line.ProductId, availableRaw, stock.DefaultUnitId, line.UnitId.Value, ct);
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
                        stockUnitCode = ctx.StockUnitCodes.GetValueOrDefault(stock.DefaultUnitId);
                    }
                }
            }

            // A unit gap is not a shortfall — we cannot make the comparison at all, so we do not
            // claim the pantry is short.
            var isShortfall = !isUnitGap && scaledQty.HasValue && availableInIngUnit < scaledQty.Value;

            return new CookLineView(
                IngredientId: ingredientId,
                LineKey: lineKey,
                PathKey: pathKey,
                IsInclusion: isInclusion,
                ProductId: line.ProductId,
                ProductName: product.Name,
                GroupHeading: groupHeading,
                ScaledQuantity: scaledQty,
                UnitCode: unitCode,
                IsUntracked: false,
                IsParent: false,
                VariantOptions: [],
                AvailableQuantity: availableInIngUnit,
                IsShortfall: isShortfall,
                IsUnitGap: isUnitGap,
                StockQuantity: stockQuantity,
                StockUnitCode: stockUnitCode);
        }
    }

    /// <summary>
    /// Walks the recipe's inclusion tree (household-scoped, cheap at household scale) to derive, for each
    /// inclusion path, the sub-recipe display name and its effective servings in THIS cook —
    /// <c>Inclusion.Servings × (servings-of-owning-recipe / owning-recipe.DefaultServings)</c>, seeded at
    /// the root by the desired servings so top-level inclusions read <c>Inclusion.Servings × ServingsScale</c>
    /// (recipe-composition.md §6, D2). Keyed by the '/'-joined <c>InclusionId</c> path, matching
    /// <see cref="ExpandedLine.PathKey"/>. Carries a defensive ancestor set (N4 rejects cycles at save).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, GroupMeta>> BuildInclusionGroupMetaAsync(
        Recipe root, int desiredServings, CancellationToken ct)
    {
        var map = new Dictionary<string, GroupMeta>();
        await WalkInclusionsAsync(root, [], desiredServings, new HashSet<Guid> { root.Id.Value }, map, ct);
        return map;
    }

    private async Task WalkInclusionsAsync(
        Recipe recipe, IReadOnlyList<Guid> path, decimal servingsBeingMade,
        HashSet<Guid> ancestors, Dictionary<string, GroupMeta> map, CancellationToken ct)
    {
        // How many DefaultServings-sized batches of THIS recipe are being made.
        var batches = servingsBeingMade / recipe.DefaultServings;
        foreach (var inc in recipe.Inclusions)
        {
            if (ancestors.Contains(inc.SubRecipeId.Value))
                continue; // defensive cycle guard — N4 prevents this at save
            var sub = await recipes.GetByIdAsync(inc.SubRecipeId, ct);
            if (sub is null)
                continue; // defensive: a missing sub is dropped from the group headers

            var subServings = inc.Servings * batches;
            var subPath = new List<Guid>(path) { inc.Id.Value };
            var pathKey = string.Join('/', subPath);
            map[pathKey] = new GroupMeta(sub.Name, subServings);

            ancestors.Add(inc.SubRecipeId.Value);
            await WalkInclusionsAsync(sub, subPath, subServings, ancestors, map, ct);
            ancestors.Remove(inc.SubRecipeId.Value);
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var desiredServings = Servings is > 0 ? Servings.Value : 1;

        // Resolve the current user id from the HttpContext.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Forbid();

        // We need the recipe (and its expansion) to resolve path-qualified line identity.
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return NotFound();

        var scale = (decimal)desiredServings / recipe.DefaultServings;

        // Expand to the same flat, path-qualified line list the GET rendered (D6). Fall back to the
        // recipe's own direct ingredients on a defensive expansion failure so a normal cook still posts.
        var expandResult = await expansion.ExpandAsync(recipe.Id, ct);
        var expandedLines = expandResult.IsSuccess
            ? expandResult.Value
            : recipe.Ingredients
                .OrderBy(i => i.Ordinal)
                .Select(i => new ExpandedLine(
                    [], i.Id, recipe.Id, i.ProductId, i.Quantity, i.UnitId,
                    i.GroupHeading is null ? [] : [i.GroupHeading]))
                .ToList();

        // ── Direct-line inputs (empty path) — the pre-composition contract, unchanged ─────────────
        var skippedDirect = SkippedIngredientIds.ToHashSet();
        var pickerDirect = PickerSelections
            .Where(p => p.VariantId != Guid.Empty)
            .GroupBy(p => p.IngredientId)
            .ToDictionary(g => g.Key, g => g.First().VariantId);

        // ── Inclusion-line inputs (path-qualified), keyed by lineKey ──────────────────────────────
        var skippedInclusionLines = SkippedInclusionLineKeys.ToHashSet();

        var resolutions = new List<IngredientResolution>();

        // Whole-inclusion skips (D7): one resolution per skipped inclusion path prefix.
        foreach (var pathKey in SkippedInclusions.Distinct())
        {
            var path = ParseInclusionPath(pathKey);
            if (path.Count > 0)
                resolutions.Add(IngredientResolution.WholeInclusionSkip(path));
        }

        foreach (var line in expandedLines)
        {
            var ingId = line.IngredientId;
            var isDirect = line.Path.Count == 0;
            var lineKey = isDirect ? ingId.Value.ToString() : $"{line.PathKey}|{ingId.Value}";

            // Per-line skip (C9). Direct lines read SkippedIngredientIds; inclusion lines read the
            // path-qualified SkippedInclusionLineKeys. Path is carried so the resolution keys 1:1 with
            // the expanded line (empty path for direct lines).
            var isSkipped = isDirect
                ? skippedDirect.Contains(ingId.Value)
                : skippedInclusionLines.Contains(lineKey);
            if (isSkipped)
            {
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: true, Allocations: [], Path: line.Path));
                continue;
            }

            var scaledQty = line.Quantity.HasValue ? line.Quantity.Value * scale : 0m;

            // Quantity override (C9 modify): direct lines read QuantityOverrides (keyed by IngredientId);
            // inclusion lines read InclusionQuantityOverrides (keyed by lineKey).
            decimal overrideQty;
            var hasOverride = isDirect
                ? QuantityOverrides.TryGetValue(ingId.Value, out overrideQty) && overrideQty >= 0m
                : InclusionQuantityOverrides.TryGetValue(lineKey, out overrideQty) && overrideQty >= 0m;
            var effectiveQty = hasOverride ? overrideQty : scaledQty;

            // Variant picker selection (C7/C11): direct lines read PickerSelections; inclusion lines read
            // InclusionPickerSelections. The empty-variant guard mirrors the direct pickerDirect filter.
            Guid chosenVariantId;
            var hasPicker = isDirect
                ? pickerDirect.TryGetValue(ingId.Value, out chosenVariantId)
                : InclusionPickerSelections.TryGetValue(lineKey, out chosenVariantId) && chosenVariantId != Guid.Empty;

            if (hasPicker)
            {
                // User selected a specific variant; allocate the effective quantity to it.
                var unitId = line.UnitId ?? Guid.Empty;
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: false, Allocations:
                [
                    new VariantAllocation(chosenVariantId, effectiveQty, unitId),
                ], Path: line.Path));
            }
            else if (hasOverride)
            {
                // Leaf/line with a quantity override: emit an explicit resolution so CookRecipe uses the
                // user-entered quantity rather than the scaled default.
                var unitId = line.UnitId ?? Guid.Empty;
                resolutions.Add(new IngredientResolution(ingId, IsSkipped: false, Allocations:
                [
                    new VariantAllocation(line.ProductId, effectiveQty, unitId),
                ], Path: line.Path));
            }
            // No entry for this line → default auto-selection in CookRecipe service.
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

    /// <summary>
    /// Parses a '/'-joined <c>InclusionId</c> path string (as posted for a whole-inclusion skip) back
    /// into the <see cref="InclusionId"/> chain. Malformed segments are dropped defensively.
    /// </summary>
    private static IReadOnlyList<InclusionId> ParseInclusionPath(string pathKey)
    {
        if (string.IsNullOrEmpty(pathKey))
            return [];
        var result = new List<InclusionId>();
        foreach (var segment in pathKey.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(segment, out var g))
                result.Add(InclusionId.From(g));
        }
        return result;
    }

    /// <summary>Resolved catalog/stock context shared by <see cref="BuildLineViewAsync"/> across all lines.</summary>
    private sealed record CookRenderContext(
        IReadOnlyDictionary<Guid, CatalogProduct> CatalogById,
        IReadOnlyDictionary<Guid, string> UnitCodes,
        IReadOnlyDictionary<Guid, CatalogProductSummary> VariantSummaries,
        IReadOnlyDictionary<Guid, Guid> VariantDefaultUnits,
        IReadOnlyDictionary<Guid, ProductStock> StockById,
        IReadOnlyDictionary<Guid, string> StockUnitCodes,
        IReadOnlyDictionary<Guid, string> VariantUnitCodes);

    /// <summary>Per-inclusion-path display metadata (sub-recipe name + effective servings in this cook).</summary>
    private sealed record GroupMeta(string SubName, decimal EffectiveServings);
}

// ── View models ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level view model for the Cook confirmation page.</summary>
/// <param name="Lines">The recipe's own direct ingredient lines (empty path) — rendered in the main card.</param>
/// <param name="InclusionGroups">
/// One group per inclusion (recipe-composition.md §6), each with a sub-recipe header + effective servings
/// and its expanded lines. Empty for a recipe with no inclusions (which then renders exactly as before).
/// </param>
public sealed record CookViewModel(
    Guid RecipeId,
    string RecipeName,
    int DesiredServings,
    int DefaultServings,
    decimal Scale,
    IReadOnlyList<CookLineView> Lines,
    IReadOnlyList<CookInclusionGroupView> InclusionGroups);

/// <summary>
/// One inclusion group on the Cook page (recipe-composition.md §6, D6/D7): the sub-recipe's expanded
/// lines under a header carrying its display name and effective servings, plus a whole-inclusion skip.
/// </summary>
/// <param name="PathKey">The '/'-joined <c>InclusionId</c> path — the whole-inclusion skip token.</param>
/// <param name="SubRecipeName">The included recipe's display name (group header).</param>
/// <param name="EffectiveServings">Servings of the sub being made in this cook (D2), for the header.</param>
/// <param name="Lines">The sub's expanded lines with the full per-line toolkit.</param>
public sealed record CookInclusionGroupView(
    string PathKey,
    string SubRecipeName,
    decimal EffectiveServings,
    IReadOnlyList<CookLineView> Lines);

/// <summary>One ingredient line on the Cook confirmation page.</summary>
/// <param name="LineKey">
/// The Alpine/form identity of the line: the bare <c>IngredientId</c> for a direct line, or
/// <c>"{PathKey}|{IngredientId}"</c> for an expanded inclusion line. Direct lines keep the bare id so the
/// pre-composition Alpine state and form contract are byte-for-byte preserved.
/// </param>
/// <param name="PathKey">The '/'-joined <c>InclusionId</c> path (empty for a direct line).</param>
/// <param name="IsInclusion">True when the line comes from an inclusion (non-empty path).</param>
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
    string LineKey,
    string PathKey,
    bool IsInclusion,
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
