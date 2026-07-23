using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Domain;
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
    ShoppingListQueryService shoppingList,
    RecipeExpansionService expansionService,
    IQuantityFormatter quantityFormatter,
    DisplayCurrencyAccessor displayCurrency,
    ArchiveRecipe archiveRecipe) : PageModel
{
    /// <summary>Household display currency (plantry-2x6e.2) — the recipe cost meta renders through MoneyDisplay with it.</summary>
    public string DisplayCurrency { get; private set; } = "USD";

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    /// <summary>
    /// Set to <c>true</c> only by the editor's post-save redirect after a qualifying edit (plantry-qll2.3): a
    /// change to the ingredient ProductId set on a Diet-tagged recipe. Drives the deferred diet-tag nudge
    /// placeholder. A plain recipe view carries no such flag, so the LLM check runs only on the post-save
    /// landing — never on a browse (no corpus sweep).
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "dietNudge")]
    public bool DietNudge { get; set; }

    /// <summary>
    /// Comma-joined ids of the including PARENT recipes the editor's cheap reverse-ripple guard flagged after saving
    /// THIS recipe as a sub (recipe-composition.md D10 / plantry-fqb0.7). Set only on the editor's post-save redirect
    /// (<c>?rippleParents=g1,g2</c>); a plain view carries none, so the per-parent LLM check runs only on the save
    /// landing — never on a browse. Parsed into <see cref="RippleParentIds"/>.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "rippleParents")]
    public string? RippleParents { get; set; }

    /// <summary>
    /// Parsed, de-duplicated parent ids from <see cref="RippleParents"/> (empty on a plain view). Each renders one
    /// deferred htmx ripple-nudge placeholder on this sub's landing (recipe-composition.md D10).
    /// </summary>
    public IReadOnlyList<Guid> RippleParentIds { get; private set; } = [];

    public RecipeDetailView Recipe { get; private set; } = null!;

    /// <summary>
    /// Set when the Archive action is blocked by N5 (recipe-composition.md D12 — "used by N recipes"), so
    /// the page re-renders with the domain error as a validation banner rather than a 500 or silent no-op.
    /// </summary>
    public string? SaveError { get; private set; }

    /// <summary>
    /// The fulfilment rail card view model rendered on first page load (at default servings), including
    /// the server-computed "Add missing" / "Add all" button label states (plantry-gsj). The servings
    /// stepper swaps this via <see cref="OnGetFulfilmentAsync"/> and the add buttons swap it via the
    /// POST handlers, so the buttons always reflect server truth (no client-side flag flip).
    /// </summary>
    public DetailsFulfilmentCardModel FulfilmentCard { get; private set; } = null!;

    /// <summary>
    /// True when the user has just saved an edit that warrants a diet-tag contradiction check (plantry-qll2.3):
    /// the editor's post-save redirect set the <c>?dietNudge=true</c> flag (bound into <see cref="DietNudge"/>) for
    /// this recipe. Drives the deferred htmx nudge placeholder in the view — the LLM runs only on this post-save
    /// landing, never on a plain recipe view (no corpus sweep).
    /// </summary>
    public bool ShowDietNudgeCheck { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await LoadDetailAsync(ct) ? Page() : NotFound();

    /// <summary>
    /// Archives the recipe with the N5 guard (recipe-composition.md D12). On success redirects to the browse
    /// list; when the archive is blocked because the recipe is still included by others, the page re-renders
    /// with the domain error surfaced as a validation banner (no 500). Returns 404 if the recipe is gone.
    /// </summary>
    public async Task<IActionResult> OnPostArchiveAsync(CancellationToken ct)
    {
        var result = await archiveRecipe.ExecuteAsync(RecipeId.From(Id), ct);
        if (result.IsSuccess)
            return RedirectToPage("/Recipes/Index");

        // Blocked (N5) or not found — re-render Details so the error shows in context.
        if (!await LoadDetailAsync(ct))
            return NotFound();
        SaveError = result.Error.Description;
        return Page();
    }

    /// <summary>
    /// Loads the recipe and builds every view model the Details page renders (ingredient rows, fulfilment
    /// card, inclusions). Returns false when the recipe does not exist (or is filtered by household RLS).
    /// Shared by the GET path and the archive-failure re-render.
    /// </summary>
    private async Task<bool> LoadDetailAsync(CancellationToken ct)
    {
        var id = RecipeId.From(Id);
        var recipe = await recipes.GetByIdAsync(id, ct);
        if (recipe is null) return false;

        // Household display currency for the cost meta (plantry-2x6e.2); resolved once per request.
        DisplayCurrency = await displayCurrency.GetAsync(ct);

        // Diet-tag nudge (plantry-qll2.3): render the deferred check only when the editor's post-save redirect set
        // ?dietNudge=true. A plain view of the recipe carries no flag, so no LLM check runs on a browse.
        ShowDietNudgeCheck = DietNudge;

        // Reverse ripple (recipe-composition.md D10 / plantry-fqb0.7): the editor's post-save redirect lists the
        // including PARENTS its cheap guard flagged in ?rippleParents. Parse them into placeholders here; a plain
        // view carries none, so the per-parent LLM check runs only on this save landing (no corpus sweep).
        RippleParentIds = ParseRippleParents(RippleParents);

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
        // Per-unit fraction/decimal display style (quantity-display.md Q1/Q4, plantry-95w5) — threaded
        // onto each ingredient row so the client-side servings-stepper rescale can snap fractions too.
        var unitStyles = await catalog.ResolveUnitDisplayStylesAsync(unitIds, ct);

        // Resolve tag names via the in-context tag repository.
        var tagIds = recipe.Tags.Select(rt => rt.TagId).ToList();
        var tagLookup = await tags.ResolveNamesAsync(tagIds, ct);

        // Expand the recipe ONCE (D4 single choke point) — the flat product-level view drives live
        // fulfillment, cost, and shopping targets (recipe-composition.md §7) AND the inclusion previews below,
        // so nested subs' products are reflected everywhere. On the defensive-cycle / missing-sub error path
        // the previews and expanded set are simply empty; a flat recipe expands to its own ingredients.
        var expandResult = await expansionService.ExpandAsync(id, ct);
        IReadOnlyList<ExpandedLine> expandedLines = expandResult.IsSuccess ? expandResult.Value : [];
        var effectiveLines = expandedLines.AggregateByProductAndUnit();

        // Compute live fulfillment and cost over the expanded view at default servings (P2-2a / P2-2b).
        // Sequential awaits required — FulfillmentService and CostingService share the scoped EF
        // DbContexts; concurrent Task.WhenAll would throw InvalidOperationException (see BrowseRecipesQuery.cs:51-53).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeExpandedAsync(
            effectiveLines, recipe.DefaultServings, recipe.DefaultServings, today, ct);
        var cost = await costingService.ComputeExpandedAsync(effectiveLines, recipe.DefaultServings, recipe.DefaultServings, ct);

        // Vulgar-fraction display for each ingredient amount (quantity-display.md §7 — Details renders at
        // 1×: FormatAmount in the authored unit's DisplayStyle, no simplification). Keyed by IngredientId.
        var displayQuantities = await ComputeIngredientDisplayAsync(recipe, ct);

        // Ingredient + inclusion rows, unioned by ordinal and grouped by GroupHeading (recipe-composition.md
        // §5, plantry-4037): each inclusion renders in ordinal position as a collapsible roll-up row, the
        // ingredient-row grammar reused verbatim for both. Built ONCE here — fulfillment/expandedLines are
        // identical for the full-page view and the initial fulfilment card (both render at DefaultServings),
        // so reusing the same list avoids a second sub-recipe-name lookup + formatter round-trip per load.
        var ingredientGroups = await BuildIngredientGroupsAsync(
            recipe, productLookup, unitLookup, unitStyles, expandedLines, fulfillment, displayQuantities, ct);

        // Server-compute the add-to-shopping button label states from the true delta between the
        // recipe's shortfall/required sets and its existing contribution slice (plantry-gsj).
        FulfilmentCard = await BuildCardModelAsync(
            recipe, recipe.DefaultServings, effectiveLines, ingredientGroups, fulfillment, oob: false, summary: null, ct);

        // Missing-price ingredient names for the cost-stat popover (plantry-zxo4): resolved from the
        // ingredient rows already built above, not a fresh Catalog read.
        var missingPriceIngredients = BuildMissingPriceIngredients(cost, ingredientGroups);

        Recipe = RecipeDetailView.From(
            recipe, tagLookup, ParseDirections(recipe.Directions), fulfillment, cost, ingredientGroups, missingPriceIngredients);

        return true;
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

        var (productLookup, unitLookup, unitStyles, effectiveLines, expandedLines, fulfillment) = await ResolveFulfilmentAsync(recipe, desiredServings, ct);

        // Oob: true so the partial's #rd-ing-rows block carries hx-swap-oob="true" and htmx
        // replaces the ingredient rows in place alongside the primary #rd-fulf-card swap. Recomputing
        // the button label at the new servings gives the top-up state ("Add N more") rather than a
        // blind full re-add when servings grew (plantry-gsj).
        // Ingredient amounts render at 1× server-side (the servings stepper scales client-side, Q4/§7).
        var displayQuantities = await ComputeIngredientDisplayAsync(recipe, ct);
        var ingredientGroups = await BuildIngredientGroupsAsync(
            recipe, productLookup, unitLookup, unitStyles, expandedLines, fulfillment, displayQuantities, ct);
        var vm = await BuildCardModelAsync(
            recipe, desiredServings, effectiveLines, ingredientGroups, fulfillment, oob: true, summary: null, ct);

        return Partial("_DetailsFulfilmentCard", vm);
    }

    /// <summary>
    /// Resolves the catalog lookups and the fresh fulfilment result for a recipe at a given serving
    /// count — the shared read every card render needs (initial load, servings refresh, post-add swap).
    /// <see cref="ExpandedLine"/>s are returned alongside the aggregated <see cref="EffectiveIngredient"/>s
    /// because the ingredient-row grouping (plantry-4037) needs the raw, unaggregated Path to attribute
    /// each expanded line back to its owning top-level inclusion (Path[0] == inclusion id, D6).
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, CatalogProductSummary> Products,
                        IReadOnlyDictionary<Guid, string> Units,
                        IReadOnlyDictionary<Guid, bool> UnitStyles,
                        IReadOnlyList<EffectiveIngredient> EffectiveLines,
                        IReadOnlyList<ExpandedLine> ExpandedLines,
                        ExpandedFulfillmentResult Fulfillment)> ResolveFulfilmentAsync(
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
        // Per-unit fraction/decimal display style (quantity-display.md Q1/Q4, plantry-95w5) — see
        // LoadDetailAsync for why this rides alongside the unit-code lookup.
        var unitStyles = await catalog.ResolveUnitDisplayStylesAsync(unitIds, ct);

        // Expand + aggregate so fulfillment and the shopping targets reflect included recipes' products
        // (recipe-composition.md §7). A flat recipe expands to its own ingredients.
        var expandResult = await expansionService.ExpandAsync(recipe.Id, ct);
        IReadOnlyList<ExpandedLine> expandedLines = expandResult.IsSuccess ? expandResult.Value : [];
        var effectiveLines = expandedLines.AggregateByProductAndUnit();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fulfillment = await fulfillmentService.ComputeExpandedAsync(
            effectiveLines, recipe.DefaultServings, servings, today, ct);
        return (productLookup, unitLookup, unitStyles, effectiveLines, expandedLines, fulfillment);
    }

    /// <summary>
    /// Builds the fulfilment card view model — the per-ingredient rows plus the server-computed
    /// "Add missing" / "Add all" button label states (plantry-gsj). The label states come from the
    /// true delta between the recipe's shortfall/required target sets at <paramref name="servings"/>
    /// and its existing contribution slice, read via Shopping's public query surface (the Web page is
    /// the composition root, mirroring plantry-yt0m). <paramref name="summary"/> is non-null only on a
    /// post-add re-render, surfacing "Added X · Y already on your list · Z checked off".
    /// <paramref name="ingredientGroups"/> is built by the caller (<see cref="BuildIngredientGroupsAsync"/>)
    /// rather than here, so <see cref="LoadDetailAsync"/> can build it exactly ONCE and share it with both
    /// this card and the full-page <see cref="RecipeDetailView"/> — it needs a sub-recipe-name lookup and a
    /// formatter round-trip (plantry-4037), so building it twice per initial load would double both.
    /// </summary>
    private async Task<DetailsFulfilmentCardModel> BuildCardModelAsync(
        Recipe recipe,
        int servings,
        IReadOnlyList<EffectiveIngredient> effectiveLines,
        IReadOnlyList<IngredientGroupView> ingredientGroups,
        ExpandedFulfillmentResult fulfillment,
        bool oob,
        ShoppingSyncOutcome? summary,
        CancellationToken ct)
    {
        // Target sets computed via the same shared calculator the write path uses, so the label and the
        // synced set cannot drift (plantry-gsj). The "Add all" set spans the whole expanded product set
        // (including sub products), so resolve track_stock across every effective product — not just the
        // direct-ingredient productLookup, which omits sub-recipe products.
        var effectiveTrackedIds = effectiveLines
            .Where(l => l.Quantity.HasValue && l.UnitId.HasValue)
            .Select(l => l.ProductId)
            .Distinct()
            .ToList();
        var effectiveSummaries = await catalog.ResolveSummariesAsync(effectiveTrackedIds, ct);
        var trackedProductIds = effectiveSummaries.Where(kv => kv.Value.TrackStock).Select(kv => kv.Key).ToHashSet();
        var missingTargets = RecipeShoppingTargets.Missing(effectiveLines, fulfillment, recipe.DefaultServings, servings);
        var allTargets = RecipeShoppingTargets.All(effectiveLines, trackedProductIds, recipe.DefaultServings, servings);

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
    /// Builds the fulfilment-keyed row list — a UNION of direct ingredient rows and collapsible inclusion
    /// roll-up rows (recipe-composition.md §5, plantry-4037), ordered by the shared N3 ordinal space and
    /// grouped by GroupHeading exactly like a flat ingredient list. Shared by the full-page view
    /// (<see cref="RecipeDetailView.From"/>) and the card partial (stepper OOB refresh, post-add
    /// re-render) so the grouping/status/roll-up logic lives in one place.
    /// <para>
    /// A product appearing in BOTH the parent and a sub shows the SAME aggregate verdict in both rows —
    /// <paramref name="fulfillment"/> is keyed on the (ProductId, UnitId) grain (D14), read identically by
    /// a direct row and a child row. This is intentional (design item 7), not a bug.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<IngredientGroupView>> BuildIngredientGroupsAsync(
        Recipe recipe,
        IReadOnlyDictionary<Guid, CatalogProductSummary> productLookup,
        IReadOnlyDictionary<Guid, string> unitLookup,
        IReadOnlyDictionary<Guid, bool> unitStyles,
        IReadOnlyList<ExpandedLine> expandedLines,
        ExpandedFulfillmentResult fulfillment,
        IReadOnlyDictionary<Guid, string> displayQuantities,
        CancellationToken ct)
    {
        // The expanded fulfillment is keyed on the (ProductId, UnitId) aggregation grain (recipe-composition.md
        // §7) — shared by direct rows AND inclusion child rows below.
        var fulfillmentByKey = fulfillment.Lines.ToDictionary(l => (l.ProductId, l.UnitId));

        IngredientItemView BuildItem(
            Guid productId, Guid? unitId, decimal? quantity,
            IReadOnlyDictionary<Guid, CatalogProductSummary> products,
            IReadOnlyDictionary<Guid, string> units,
            IReadOnlyDictionary<Guid, bool> styles,
            string? displayQuantity)
        {
            products.TryGetValue(productId, out var product);
            var isUntracked = product is { TrackStock: false };
            // Untracked-ness (Product.TrackStock) is orthogonal to whether the recipe author supplied a
            // quantity (R5: Quantity/UnitId are both-set or both-null) — "to taste" (null qty/unit) is one
            // valid case of an untracked ingredient, not the only one. Pass the authored quantity/unit
            // through unconditionally; only the line's own null qty/unit suppresses the amount.
            var unitCode = unitId is { } uid ? units.GetValueOrDefault(uid) : null;
            // Fraction/decimal display style (quantity-display.md Q1/Q4, plantry-95w5) — read alongside
            // the unit code so _IngredientRow can tell the client-side rescale whether to attempt the
            // vulgar-fraction snap once the servings stepper moves the amount off scale 1.
            var isFractionStyle = unitId.HasValue && styles.GetValueOrDefault(unitId.Value);
            fulfillmentByKey.TryGetValue((productId, unitId), out var f);
            return new IngredientItemView(
                ProductName: product?.Name ?? "(unknown product)",
                ProductId: productId,
                Quantity: quantity,
                UnitCode: unitCode,
                IsUntracked: isUntracked,
                Status: f?.Status ?? IngredientStatus.Untracked,
                ExpiresWithinDays: f?.ExpiresWithinDays,
                UnitMismatch: f?.UnitMismatch ?? false,
                DisplayQuantity: displayQuantity,
                IsFractionStyle: isFractionStyle);
        }

        // Inclusion support data (sub name/DefaultServings, expanded-product lookups, batched child
        // display-quantity formatting) — resolved only when the recipe actually has inclusions, exactly
        // mirroring the previous BuildInclusionViewsAsync's up-front resolution block.
        var subInfo = new Dictionary<RecipeId, (string Name, int DefaultServings)>();
        IReadOnlyDictionary<Guid, CatalogProductSummary> exProductLookup = new Dictionary<Guid, CatalogProductSummary>();
        IReadOnlyDictionary<Guid, string> exUnitLookup = new Dictionary<Guid, string>();
        IReadOnlyDictionary<Guid, bool> exUnitStyles = new Dictionary<Guid, bool>();
        IReadOnlyDictionary<string, FormattedQuantity> childDisplay = new Dictionary<string, FormattedQuantity>();
        var childLinesByInclusion = new Dictionary<InclusionId, List<ExpandedLine>>();

        if (recipe.Inclusions.Count > 0)
        {
            foreach (var subId in recipe.Inclusions.Select(i => i.SubRecipeId).Distinct())
            {
                var sub = await recipes.GetByIdAsync(subId, ct);
                if (sub is not null) subInfo[subId] = (sub.Name, sub.DefaultServings);
            }

            var exProductIds = expandedLines.Select(l => l.ProductId).Distinct().ToList();
            exProductLookup = exProductIds.Count > 0
                ? await catalog.ResolveSummariesAsync(exProductIds, ct)
                : new Dictionary<Guid, CatalogProductSummary>();
            var exUnitIds = expandedLines.Where(l => l.UnitId is not null).Select(l => l.UnitId!.Value).Distinct().ToList();
            exUnitLookup = exUnitIds.Count > 0
                ? await catalog.ResolveUnitCodesAsync(exUnitIds, ct)
                : new Dictionary<Guid, string>();
            exUnitStyles = exUnitIds.Count > 0
                ? await catalog.ResolveUnitDisplayStylesAsync(exUnitIds, ct)
                : new Dictionary<Guid, bool>();

            foreach (var inc in recipe.Inclusions)
                childLinesByInclusion[inc.Id] = expandedLines.Where(l => l.Path.Count > 0 && l.Path[0] == inc.Id).ToList();

            // Batch every child line's vulgar-fraction display into ONE formatter call across ALL inclusions
            // (mirrors ComputeIngredientDisplayAsync — never per inclusion or per line). Details renders at
            // 1× so Simplify is false (quantity-display.md §7). Child lines carry no ingredient id, so key
            // by a synthetic "{inclusionId}:{lineIndex}" stable within this render.
            var childRequests = new List<QuantityFormatRequest>();
            foreach (var (incId, lines) in childLinesByInclusion)
            {
                for (var idx = 0; idx < lines.Count; idx++)
                {
                    var l = lines[idx];
                    // Untracked-ness (Product.TrackStock) is orthogonal to whether a quantity was
                    // authored (R5, plantry-cbww) — an untracked child with a real quantity/unit still
                    // gets the vulgar-fraction display formatting, not just the plain decimal fallback.
                    if (l.Quantity.HasValue && l.UnitId.HasValue)
                        childRequests.Add(new QuantityFormatRequest($"{incId.Value}:{idx}", l.Quantity.Value, l.UnitId.Value, Simplify: false));
                }
            }
            childDisplay = childRequests.Count > 0
                ? await quantityFormatter.FormatAsync(childRequests, ct)
                : new Dictionary<string, FormattedQuantity>();
        }

        InclusionRowView BuildInclusionRow(Inclusion inc)
        {
            subInfo.TryGetValue(inc.SubRecipeId, out var info);
            var subDefaultServings = info.DefaultServings <= 0 ? 1 : info.DefaultServings;
            var childLines = childLinesByInclusion.GetValueOrDefault(inc.Id, []);

            var children = new List<IngredientItemView>(childLines.Count);
            for (var idx = 0; idx < childLines.Count; idx++)
            {
                var l = childLines[idx];
                var displayQuantity = childDisplay.GetValueOrDefault($"{inc.Id.Value}:{idx}")?.Amount;
                children.Add(BuildItem(l.ProductId, l.UnitId, l.Quantity, exProductLookup, exUnitLookup, exUnitStyles, displayQuantity));
            }

            var (worstStatus, chip, inStockCount, trackedTotal, worstExpiry) =
                ComputeInclusionRollup(children, childLines);

            return new InclusionRowView(
                InclusionId: inc.Id.Value,
                SubRecipeId: inc.SubRecipeId.Value,
                SubName: info.Name ?? "(unknown recipe)",
                Servings: inc.Servings,
                SubDefaultServings: subDefaultServings,
                WorstStatus: worstStatus,
                Chip: chip,
                TrackedInStockCount: inStockCount,
                TrackedTotalCount: trackedTotal,
                WorstExpiresWithinDays: worstExpiry,
                Children: children);
        }

        IngredientRowView BuildIngredientRow(Ingredient i)
        {
            // Pretty (vulgar-fraction) rendering of the authored quantity at 1× (quantity-display.md
            // Q1/§7) — untracked-ness is orthogonal to whether a quantity was authored (R5, plantry-cbww),
            // so this no longer special-cases untracked lines. Falls back to the canonical decimal
            // formatter only when the formatter had no entry (e.g. a null quantity / unknown unit).
            var displayQuantity = i.Quantity.HasValue
                ? displayQuantities.GetValueOrDefault(i.Id.Value, IngredientAmount.Format(i.Quantity.Value))
                : null;
            return new IngredientRowView(BuildItem(i.ProductId, i.UnitId, i.Quantity, productLookup, unitLookup, unitStyles, displayQuantity));
        }

        // Union the two line types by their shared N3 ordinal space (IngredientOrdinalMerge assigns each
        // contiguous, unique positions at save time, so a plain OrderBy(Ordinal) needs no tie-break here),
        // then group by GroupHeading exactly as a flat ingredient list would — an inclusion slots into its
        // authored section like any other line.
        var merged = recipe.Ingredients
            .Select(i => (i.Ordinal, i.GroupHeading, Row: (IngredientLineRowView)BuildIngredientRow(i)))
            .Concat(recipe.Inclusions.Select(inc => (inc.Ordinal, inc.GroupHeading, Row: (IngredientLineRowView)BuildInclusionRow(inc))))
            .OrderBy(x => x.Ordinal)
            .ToList();

        return merged
            .GroupBy(x => x.GroupHeading)
            .Select(g => new IngredientGroupView(Heading: g.Key, Items: g.Select(x => x.Row).ToList()))
            .ToList();
    }

    /// <summary>
    /// Resolves <see cref="CostPerServing.MissingPriceProductIds"/> into display names for the cost-stat
    /// popover (plantry-zxo4, Partial and None states) — reuses the <see cref="IngredientItemView.ProductName"/>
    /// already carried by <paramref name="ingredientGroups"/> (direct rows AND inclusion child rows) rather
    /// than issuing a new Catalog read. A product line can appear twice in <c>MissingPriceProductIds</c> when
    /// it is costed across two different units (aggregated by (ProductId, UnitId)), so the result is
    /// de-duplicated by product id, preserving first-appearance order.
    /// </summary>
    private static IReadOnlyList<MissingPriceIngredientView> BuildMissingPriceIngredients(
        CostPerServing cost, IReadOnlyList<IngredientGroupView> ingredientGroups)
    {
        if (cost.MissingPriceProductIds.Count == 0) return [];

        var nameByProductId = new Dictionary<Guid, string>();
        void Collect(IngredientItemView item) => nameByProductId.TryAdd(item.ProductId, item.ProductName);

        foreach (var row in ingredientGroups.SelectMany(g => g.Items))
        {
            switch (row)
            {
                case IngredientRowView direct:
                    Collect(direct.Item);
                    break;
                case InclusionRowView inclusion:
                    foreach (var child in inclusion.Children) Collect(child);
                    break;
            }
        }

        var seen = new HashSet<Guid>();
        var result = new List<MissingPriceIngredientView>();
        foreach (var productId in cost.MissingPriceProductIds)
        {
            if (!seen.Add(productId)) continue;
            result.Add(new MissingPriceIngredientView(
                productId, nameByProductId.GetValueOrDefault(productId, "(unknown product)")));
        }
        return result;
    }

    /// <summary>
    /// Computes the vulgar-fraction display string for each tracked ingredient amount, keyed by
    /// <see cref="Ingredient.Id"/> (quantity-display.md §7). Details always renders at 1× (the recipe's
    /// default servings — the servings stepper scales client-side, Q4), so no unit simplification runs:
    /// each amount is presented in its authored unit's <c>DisplayStyle</c> via the anti-corruption
    /// <see cref="IQuantityFormatter"/> port, keeping this page free of any direct Catalog unit load.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ComputeIngredientDisplayAsync(Recipe recipe, CancellationToken ct)
    {
        var requests = recipe.Ingredients
            .Where(i => i.Quantity.HasValue && i.UnitId.HasValue)
            .Select(i => new QuantityFormatRequest(i.Id.Value.ToString(), i.Quantity!.Value, i.UnitId!.Value, Simplify: false))
            .ToList();
        if (requests.Count == 0)
            return new Dictionary<Guid, string>();

        var formatted = await quantityFormatter.FormatAsync(requests, ct);
        return formatted.ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value.Amount);
    }

    /// <summary>Formats an inclusion serving count for display: "1 serving" / "2 servings" / "0.5 servings".</summary>
    public static string FormatServings(decimal servings)
    {
        var n = servings.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return n + (servings == 1m ? " serving" : " servings");
    }

    /// <summary>
    /// Formats the batch-fraction hint for an inclusion (recipe-composition.md D2): batch = servings ÷ the
    /// sub's DefaultServings. Common fractions render as a glyph ("½ batch"); otherwise a cleaned decimal
    /// ("1.5 batches"). Empty when the ratio cannot be computed.
    /// </summary>
    public static string FormatBatchHint(decimal servings, int subDefaultServings)
    {
        if (subDefaultServings <= 0 || servings <= 0) return "";
        var b = servings / subDefaultServings;
        // Glyph logic now lives once, in QuantityDisplay.FormatAmount (quantity-display.md §1/§7): batches
        // are inherently a fraction-styled quantity, so the same vulgar-fraction vocabulary renders "½
        // batch", "⅓ batch", and "1½ batches" without a bespoke switch. FormatAmount falls back to its
        // 0.### decimal when the ratio snaps to no vocabulary fraction (e.g. "1.4 batches").
        var formatted = QuantityDisplay.FormatAmount(b, DisplayStyle.Fraction);
        // Pluralise off the RENDERED magnitude, not the raw ratio: FormatAmount snaps a remainder ≤ 0.01
        // down to the whole number, so a ratio like 1.008 renders "1" — the suffix must read "1 batch",
        // not the contradictory "1 batches" that keying off Math.Round(b, 2) would give. Singular for a
        // proper-fraction batch ("½ batch") and for a magnitude that renders as "1"; plural otherwise.
        return formatted + (b < 1m || formatted == "1" ? " batch" : " batches");
    }

    /// <summary>
    /// Computes an inclusion roll-up row's worst-of-children verdict (plantry-4037, plantry-j4cx) — pure and
    /// synchronous, extracted from <c>BuildInclusionRow</c> for direct L1 unit coverage. <paramref
    /// name="children"/> and <paramref name="childLines"/> are index-aligned (same length, same order) — the
    /// dedup key comes from <paramref name="childLines"/>[i] while the fulfillment status comes from
    /// <paramref name="children"/>[i].
    /// <para>
    /// Roll-up stats are over DISTINCT tracked children (untracked staples excluded, matching the ingredient
    /// row's own untracked treatment) — worst-of-children status (miss &gt; low &gt; have), "N of M tracked
    /// in your pantry" (N = fully in stock, M = distinct tracked), and the worst (soonest) expiry across
    /// EVERY child — deliberately NOT deduped — so urgency is never hidden behind the collapsed fold (design
    /// item 6). Interpretation: "tracked" = distinct (ProductId, UnitId) rather than raw line count, so a sub
    /// that lists the same product twice does not inflate the denominator.
    /// </para>
    /// </summary>
    public static (IngredientStatus WorstStatus, RollupChipView? Chip, int InStockCount, int TrackedTotal, int? WorstExpiresWithinDays)
        ComputeInclusionRollup(IReadOnlyList<IngredientItemView> children, IReadOnlyList<ExpandedLine> childLines)
    {
        var seenKeys = new HashSet<(Guid ProductId, Guid? UnitId)>();
        var trackedTotal = 0;
        var inStockCount = 0;
        var lowCount = 0;
        var missCount = 0;
        for (var idx = 0; idx < childLines.Count; idx++)
        {
            var item = children[idx];
            if (item.IsUntracked) continue;
            if (!seenKeys.Add((childLines[idx].ProductId, childLines[idx].UnitId))) continue;
            trackedTotal++;
            switch (item.Status)
            {
                case IngredientStatus.InStock: inStockCount++; break;
                case IngredientStatus.Low: lowCount++; break;
                case IngredientStatus.Missing: missCount++; break;
            }
        }
        var worstStatus = trackedTotal == 0 ? IngredientStatus.Untracked
            : missCount > 0 ? IngredientStatus.Missing
            : lowCount > 0 ? IngredientStatus.Low
            : IngredientStatus.InStock;
        RollupChipView? chip = missCount > 0
            ? new RollupChipView($"{missCount} to buy", IngredientStatus.Missing)
            : lowCount > 0
                ? new RollupChipView($"{lowCount} low", IngredientStatus.Low)
                : null;
        var expiryDays = children.Where(c => c.ExpiresWithinDays.HasValue).Select(c => c.ExpiresWithinDays!.Value).ToList();
        int? worstExpiry = expiryDays.Count > 0 ? expiryDays.Min() : null;

        return (worstStatus, chip, inStockCount, trackedTotal, worstExpiry);
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
        var (productLookup, unitLookup, unitStyles, effectiveLines, expandedLines, fulfillment) = await ResolveFulfilmentAsync(recipe, desiredServings, ct);

        var displayQuantities = await ComputeIngredientDisplayAsync(recipe, ct);
        var ingredientGroups = await BuildIngredientGroupsAsync(
            recipe, productLookup, unitLookup, unitStyles, expandedLines, fulfillment, displayQuantities, ct);
        var vm = await BuildCardModelAsync(
            recipe, desiredServings, effectiveLines, ingredientGroups, fulfillment, oob: false, summary: outcome, ct);

        return Partial("_DetailsFulfilmentCard", vm);
    }

    /// <summary>
    /// Parses the comma-joined <c>?rippleParents</c> redirect token into distinct parent recipe ids (D10). Blank,
    /// malformed, or duplicate segments are dropped; empty/null input yields an empty list — so a plain view (no
    /// token) renders no ripple placeholders. The ids are advisory candidates only; the deferred fragment re-runs
    /// the full gate + check per parent, so a stale or foreign id simply resolves to nothing.
    /// </summary>
    private static IReadOnlyList<Guid> ParseRippleParents(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var segment in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(segment, out var id) && seen.Add(id))
                ids.Add(id);
        }
        return ids;
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
    ExpandedFulfillmentResult Fulfillment,
    CostPerServing Cost,
    IReadOnlyList<MissingPriceIngredientView> MissingPriceIngredients)
{
    /// <summary>
    /// Builds the full-page view from an already-loaded recipe and its pre-built <paramref name="ingredientGroups"/>
    /// (recipe-composition.md §5, plantry-4037) — the union of direct ingredient rows and collapsible inclusion
    /// roll-up rows is built once by <see cref="DetailsModel.LoadDetailAsync"/> via the async
    /// <c>BuildIngredientGroupsAsync</c> (it needs sub-recipe reads + a formatter round-trip, so it cannot live in
    /// this synchronous factory) and simply threaded through here.
    /// </summary>
    internal static RecipeDetailView From(
        Recipe recipe,
        IReadOnlyDictionary<TagId, string> tagLookup,
        IReadOnlyList<DirectionBlock> directions,
        ExpandedFulfillmentResult fulfillment,
        CostPerServing cost,
        IReadOnlyList<IngredientGroupView> ingredientGroups,
        IReadOnlyList<MissingPriceIngredientView> missingPriceIngredients)
    {
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
            IngredientGroups: ingredientGroups,
            DirectionBlocks: directions,
            Fulfillment: fulfillment,
            Cost: cost,
            MissingPriceIngredients: missingPriceIngredients);
    }
}

/// <summary>
/// One missing-price ingredient link for the cost-stat popover (plantry-zxo4): the resolved product
/// name paired with its id, linking to <c>/Pantry/Products/Detail/{ProductId}</c> where plantry-3fqm's
/// Set-price sheet lives.
/// </summary>
public sealed record MissingPriceIngredientView(Guid ProductId, string ProductName);

/// <summary>A resolved tag pill for the detail view.</summary>
public sealed record TagView(Guid Id, string? Name);

/// <summary>A group of ingredient/inclusion rows sharing the same optional section heading (C6, N3).</summary>
public sealed record IngredientGroupView(
    string? Heading,
    IReadOnlyList<IngredientLineRowView> Items);

/// <summary>
/// One row in the ingredients card: a direct ingredient (<see cref="IngredientRowView"/>) or a
/// collapsible sub-recipe roll-up (<see cref="InclusionRowView"/>) — the union type
/// <see cref="IngredientGroupView.Items"/> carries so the two line types can share one ordinal/heading
/// space (recipe-composition.md §5 N3, plantry-4037).
/// </summary>
public abstract record IngredientLineRowView;

/// <summary>A direct ingredient row (unchanged shape/behaviour from before plantry-4037).</summary>
public sealed record IngredientRowView(IngredientItemView Item) : IngredientLineRowView;

/// <summary>
/// A collapsible inclusion roll-up row (plantry-4037, recipe-composition.md §5 — decided design Option C,
/// <c>.preview/included-recipes-redesign.html</c> tab C). Renders shaped exactly like an ingredient row:
/// a worst-of-children status dot (<see cref="WorstStatus"/>), an optional roll-up <see cref="Chip"/>
/// ("2 to buy" / "1 low", omitted when every tracked child is fully in stock), a "N of M tracked in your
/// pantry" sub-label (<see cref="TrackedInStockCount"/> of <see cref="TrackedTotalCount"/>), and the
/// inclusion's own servings in the amount slot. <see cref="WorstExpiresWithinDays"/> surfaces the
/// soonest child expiry as a timer chip while collapsed, since a child's own expiry badge is invisible
/// until the fold is opened. Expanding reveals <see cref="Children"/> as full-featured rows via the same
/// <c>_IngredientRow</c> partial a direct row uses — Cook precedent flattens the sub's own internal
/// sections (Cook.cshtml.cs:289), so no second heading level appears inside the fold.
/// <para>
/// A product appearing in BOTH the parent and this sub shows the SAME aggregate verdict in both rows —
/// <see cref="Children"/> read through the shared (ProductId, UnitId) fulfillment grain exactly like a
/// direct row (D14) — intentional, not a bug (design item 7).
/// </para>
/// </summary>
public sealed record InclusionRowView(
    Guid InclusionId,
    Guid SubRecipeId,
    string SubName,
    decimal Servings,
    int SubDefaultServings,
    IngredientStatus WorstStatus,
    RollupChipView? Chip,
    int TrackedInStockCount,
    int TrackedTotalCount,
    int? WorstExpiresWithinDays,
    IReadOnlyList<IngredientItemView> Children) : IngredientLineRowView;

/// <summary>
/// The collapsed inclusion row's roll-up verdict chip ("2 to buy" / "1 low") — the worst tier's count
/// only (miss takes priority over low), echoing <c>.rd-fulf-chip</c>'s tone palette at inline-row scale.
/// </summary>
public sealed record RollupChipView(string Label, IngredientStatus Tone);

/// <summary>A single ingredient row with resolved product name, quantity, unit code, tracked state, and live fulfillment status.</summary>
/// <param name="ProductId">Catalog product id (DM-3) — links the unit-mismatch popover to the product's "Add conversion" page.</param>
/// <param name="UnitMismatch">
/// Display-only (plantry-z2sr): true when the row reads Missing only because on-hand stock can't be
/// converted to the recipe unit. Swaps the "Not in your pantry" copy for an honest "can't compare units"
/// explanation + popover. Never affects <paramref name="Status"/>.
/// </param>
/// <param name="DisplayQuantity">
/// The pretty (vulgar-fraction) rendering of <paramref name="Quantity"/> at 1× in the authored unit's
/// DisplayStyle (quantity-display.md Q1/§7) — "½", "1¾", or the plain <c>0.###</c> decimal when it snaps to
/// no fraction. Null for untracked / quantity-less lines. The server render shows this; the client-side
/// servings scaler re-renders scaled amounts through <see cref="IsFractionStyle"/> so it can also snap
/// fractions (quantity-display.md Q1/Q4, plantry-95w5 — supersedes the earlier decimal-only JS twin,
/// plantry-jun6).
/// </param>
/// <param name="IsFractionStyle">
/// True when the authored unit is styled <c>DisplayStyle.Fraction</c> (quantity-display.md Q1/Q4,
/// plantry-95w5) — threaded into <c>_IngredientRow</c>'s client-side <c>fmt()</c> call so the
/// servings-stepper rescale attempts the same vulgar-fraction snap the server's 1× render does, instead
/// of always falling back to a bare decimal. False for untracked / quantity-less lines and for any
/// <c>Decimal</c>-styled unit.
/// </param>
public sealed record IngredientItemView(
    string ProductName,
    Guid ProductId,
    decimal? Quantity,
    string? UnitCode,
    bool IsUntracked,
    IngredientStatus Status,
    int? ExpiresWithinDays,
    bool UnitMismatch = false,
    string? DisplayQuantity = null,
    bool IsFractionStyle = false);

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
    ExpandedFulfillmentResult Fulfillment,
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
