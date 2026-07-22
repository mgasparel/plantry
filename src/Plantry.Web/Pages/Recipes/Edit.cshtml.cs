using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;
using Plantry.Web.Recipes;

namespace Plantry.Web.Pages.Recipes;

/// <summary>
/// Recipe author / editor page — handles both J6 (create) and J7 (edit). A null route <see cref="Id"/>
/// indicates create; a non-null id loads the existing recipe and pre-populates the form.
///
/// <para>Submit builds an <see cref="AuthorRecipeCommand"/> and delegates to <see cref="AuthorRecipe"/>.
/// <see cref="AuthorRecipeResult.Saved"/> → redirect to Details; <see cref="AuthorRecipeResult.NeedsConversion"/>
/// → re-render the form with per-row <c>NeedsConversion</c> flags so the inline ProductConversion form
/// appears (C10); <see cref="AuthorRecipeResult.Invalid"/> → re-render with a top-level error.
/// Photo upload is multipart (IFormFile) and stored as bytea per Gate 7 / recipes-domain-model.md §4.</para>
///
/// <para>Servings-change scale offer (J7 step 3): when editing a recipe with existing ingredients and the
/// user changes DefaultServings, the form shows a Proportional/Keep toggle (seg-ctrl) before submit.
/// Alpine holds whether the servings value has drifted from the original to show/hide the offer.</para>
///
/// <para>All Catalog access flows through <see cref="ICatalogProductReader"/> (the anti-corruption port);
/// this page never touches Catalog repositories directly (Gate 2). Group and category lists used
/// by the ingredient create view are loaded via <see cref="ICatalogProductReader.ListGroupsAsync"/> and
/// <see cref="ICatalogProductReader.ListCategoriesAsync"/> added in plantry-orix.</para>
/// </summary>
[Authorize]
public sealed class EditModel(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader products,
    IClock clock,
    AuthorRecipe authorRecipe,
    IUnitConverter unitConverter,
    SuggestRecipeTags suggestRecipeTags,
    DietTagNudgeService dietTagNudge,
    RecipeExpansionService recipeExpansion,
    ManageTagsService manageTags,
    IAiAssistanceGateReader aiGate,
    RecipeConversionSeedTrigger conversionSeedTrigger,
    ITenantContext tenant) : PageModel
{
    // ── Route ────────────────────────────────────────────────────────────────────

    /// <summary>Null for create (J6); a recipe's id for edit (J7).</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    public bool IsCreate => Id is null;

    // ── Form state ───────────────────────────────────────────────────────────────

    [BindProperty]
    public RecipeEditInput Input { get; set; } = new();

    // ── Edit-mode original servings (for the J7 servings-scale offer) ────────────

    /// <summary>
    /// The recipe's servings count as loaded from the DB — rendered as a hidden field and read by
    /// Alpine to show the Proportional/Keep scale offer when the user changes the servings value (J7 §3).
    /// Zero on create.
    /// </summary>
    public int OriginalServings { get; private set; }

    /// <summary>True when editing a recipe that already has at least one ingredient (enabling scale offer).</summary>
    public bool HasExistingIngredients { get; private set; }

    /// <summary>
    /// True when editing a recipe that already has a stored photo. Drives the current-photo preview in the
    /// editor's Photo block (plantry-nj0e). Mirrors how Details resolves photo presence
    /// (<see cref="RecipePhoto"/> non-null; see Details.cshtml.cs). False on create and photoless recipes.
    /// </summary>
    public bool HasPhoto { get; private set; }

    // ── Reference data for dropdowns ─────────────────────────────────────────────

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> TagOptions { get; private set; } = [];

    /// <summary>
    /// Active group products (IsParent = true) for the household, for the create-view Group combobox
    /// (plantry-orix). Passed to <see cref="ProductSearchCreateSheetViewModel.GroupOptions"/>.
    /// Loaded via <see cref="ICatalogProductReader.ListGroupsAsync"/> (the anti-corruption port).
    /// </summary>
    public IReadOnlyList<GroupOption> GroupOptions { get; private set; } = [];

    /// <summary>
    /// Category options for the Defaults collapsible in the create view (plantry-y53t / plantry-orix).
    /// Passed to <see cref="ProductSearchCreateSheetViewModel.CategoryOptions"/>.
    /// </summary>
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    // ── D13 fixed-mode servings warning ──────────────────────────────────────────

    /// <summary>
    /// Number of recipes that include the recipe being edited via an inclusion line (recipe-composition.md
    /// D13). Zero on create and for a recipe no one includes. Drives the editor's fixed-mode (Keep) servings
    /// warning: changing a serving count without proportional scaling silently changes what a serving means
    /// for every includer, so the editor names/counts them before the author confirms.
    /// </summary>
    public int IncludedByCount { get; private set; }

    /// <summary>Display names of the direct includers (D13 warning), alphabetically ordered. Empty unless the recipe is included.</summary>
    public IReadOnlyList<string> IncludedByNames { get; private set; } = [];

    // ── Top-level validation error ────────────────────────────────────────────────

    public string? SaveError { get; private set; }

    // ── Rows that need a conversion form (C10 NeedsConversion outcome) ────────────

    public IReadOnlyList<int> ConversionRequiredOrdinals { get; private set; } = [];

    // ── GET ──────────────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadReferenceDataAsync(ct);

        if (Id is { } editId)
        {
            var recipe = await recipes.GetByIdAsync(RecipeId.From(editId), ct);
            if (recipe is null) return NotFound();

            // Pre-populate scalars
            Input.Name = recipe.Name;
            Input.DefaultServings = recipe.DefaultServings;
            Input.CookTimeMinutes = recipe.CookTimeMinutes;
            Input.Source = recipe.Source;
            Input.Directions = recipe.Directions;
            Input.ScaleMode = ScaleMode.Keep;
            // Yield-on-cook (plantry-854a, recipe-composition.md §9)
            Input.YieldEnabled = recipe.HasYield;
            Input.YieldQuantity = recipe.YieldQuantity;
            Input.YieldUnitId = recipe.YieldUnitId;

            // Pre-populate tags — resolve id+name for the Alpine chip state ({id,name} objects).
            // ResolveNamesAsync includes archived tags so chips survive for tags archived since the
            // recipe was last saved (archived chips still show and can be removed; they cannot be
            // re-added from the active-only dropdown).
            var recipeTagIds = recipe.Tags.Select(rt => rt.TagId).ToList();
            var tagNameLookup = await tags.ResolveNamesAsync(recipeTagIds, ct);
            Input.TagIds = recipe.Tags
                .Select(rt => rt.TagId.Value)
                .ToList();
            Input.TagNames = recipe.Tags
                .Select(rt => tagNameLookup.GetValueOrDefault(rt.TagId) ?? rt.TagId.Value.ToString("N")[..8])
                .ToList();

            // Pre-populate ingredient rows
            var productIds = recipe.Ingredients.Select(i => i.ProductId).Distinct().ToList();
            var productLookup = await products.ResolveSummariesAsync(productIds, ct);
            var unitIds = recipe.Ingredients
                .Where(i => i.UnitId.HasValue)
                .Select(i => i.UnitId!.Value)
                .Distinct()
                .ToList();
            var unitCodeLookup = await products.ResolveUnitCodesAsync(unitIds, ct);

            // plantry-obg3: batch-resolve every ingredient product's REAL default (stock) unit in one
            // round-trip. FindManyAsync carries DefaultUnitId (unlike ResolveSummariesAsync). Seeding each
            // landed row's defaultUnitId/defaultUnitCode from the PRODUCT's default unit — not the
            // conversion-line ConversionToUnitId (Guid.Empty for any line without a stored conversion) —
            // populates the landed-row conversion ask heading before client hydration and makes the
            // client's `draft.unitId === draft.defaultUnitId` short-circuit correct for edited rows. This
            // same batch also supplies the plantry-429l unit prefill below, replacing a per-flagged-row
            // FindAsync N+1.
            var productDefaults = await products.FindManyAsync(productIds, ct);

            // Canonicalise ingredient order to match the sectioned editor + Details render
            // (plantry-vff8): ungrouped ingredients first, then each GroupHeading in first-appearance
            // order (preserving ordinal order within a section), then reassign contiguous ordinals.
            // Doing this on load means even a no-change save reconciles a legacy recipe's stored order
            // with the editor's Ungrouped-first layout — closing the editor-vs-detail snap-back.
            var canonicalIngredients = CanonicaliseSectionOrder(recipe.Ingredients);

            Input.Lines = canonicalIngredients
                .Select((ing, idx) =>
                {
                    productLookup.TryGetValue(ing.ProductId, out var p);
                    productDefaults.TryGetValue(ing.ProductId, out var pd);
                    var isTracked = p?.TrackStock ?? false;
                    var needsQtyUnit = isTracked && (ing.Quantity is null || ing.UnitId is null);
                    var defaultUnitId = pd?.DefaultUnitId ?? Guid.Empty;

                    // Part A (plantry-429l): a line authored while its product was untracked (null qty/unit
                    // legal) whose product was LATER flipped tracked is retroactively condemned by R5 on the
                    // next save. Prefill the unit for such a flagged row that has no unit, using the
                    // product's default unit (resolved in the batch above), so the user only supplies a
                    // quantity. Quantity is never prefilled (no sane default) — the null qty keeps the row
                    // flagged until the user acts.
                    var unitId = ing.UnitId;
                    if (needsQtyUnit && unitId is null && defaultUnitId != Guid.Empty)
                        unitId = defaultUnitId;

                    var unitCode = unitId.HasValue ? unitCodeLookup.GetValueOrDefault(unitId.Value) : null;
                    return new IngredientRowInput
                    {
                        Ordinal = idx,
                        ProductId = ing.ProductId,
                        ProductName = p?.Name ?? "",
                        Quantity = ing.Quantity,
                        UnitId = unitId,
                        UnitCode = unitCode,
                        // plantry-obg3: seed the row's REAL default (stock) unit so the landed-row conversion
                        // ask heading and the client same-unit short-circuit are correct before hydration.
                        DefaultUnitId = defaultUnitId,
                        GroupHeading = ing.GroupHeading,
                        IsUntracked = !isTracked,
                    };
                }).ToList();

            // Inclusion rows (recipe-composition.md §3) — pre-populate in ordinal position, resolving each
            // sub-recipe's display name and DefaultServings (needed for the batch-fraction hint, D2). Sub
            // loads are one GetByIdAsync per distinct sub — household recipe counts make this trivially cheap.
            if (recipe.Inclusions.Count > 0)
            {
                var subInfo = new Dictionary<RecipeId, (string Name, int DefaultServings)>();
                foreach (var subId in recipe.Inclusions.Select(i => i.SubRecipeId).Distinct())
                {
                    var sub = await recipes.GetByIdAsync(subId, ct);
                    if (sub is not null)
                        subInfo[subId] = (sub.Name, sub.DefaultServings);
                }

                Input.Inclusions = recipe.Inclusions
                    .OrderBy(i => i.Ordinal)
                    .Select(inc =>
                    {
                        subInfo.TryGetValue(inc.SubRecipeId, out var info);
                        return new InclusionRowInput
                        {
                            Ordinal = inc.Ordinal,
                            SubRecipeId = inc.SubRecipeId.Value,
                            SubName = info.Name ?? "(unknown recipe)",
                            Servings = inc.Servings,
                            SubDefaultServings = info.DefaultServings <= 0 ? 1 : info.DefaultServings,
                            GroupHeading = inc.GroupHeading,
                        };
                    })
                    .ToList();
            }

            // D13 — surface who includes THIS recipe, so a fixed-mode (Keep) servings change can warn that
            // it silently changes what a serving means for its includers before the author confirms.
            await LoadIncluderInfoAsync(RecipeId.From(editId), ct);

            OriginalServings = recipe.DefaultServings;
            HasExistingIngredients = recipe.Ingredients.Count > 0;
            HasPhoto = recipe.Photo is not null;
        }
        else
        {
            // Create: start with no ingredients — the user adds them via the add/edit sheet.
            Input.Lines = [];
            OriginalServings = 0;
        }

        return Page();
    }

    // ── Product search (htmx) ────────────────────────────────────────────────────

    /// <summary>
    /// Returns &lt;li role="option"&gt; markup for the ingredient product searchable-select.
    /// Called by htmx on keyup in each ingredient row's product search field.
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var hits = await products.SearchAsync(q.Trim(), ct);

        // Resolve default unit codes in one batch so the pick-product event can carry
        // defaultUnitCode alongside defaultUnit, enabling the in-sheet conversion prompt
        // to display the unit label (e.g. "g") without an extra round-trip.
        var defaultUnitIds = hits.Select(p => p.DefaultUnitId).Distinct().ToList();
        var unitCodes = defaultUnitIds.Count > 0
            ? await products.ResolveUnitCodesAsync(defaultUnitIds, ct)
            : (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();

        // Emit ranked <li> markup. The ranking label (.rk span) mirrors Intake's AlternativesStrip
        // vocabulary (best / N%) for cross-feature consistency per the design (plantry-hl4a §3).
        // The product name is stored in data-name so the click handler can set query = data-name
        // rather than textContent (which would include the .rk label text).
        // The surrounding .ingredient-row catches 'pick-product' and calls selectProduct(row, $event.detail)
        // with access to the current x-for 'row' scope — avoids the nested x-data scope conflict
        // that would occur with a single-arg $el approach.
        var html = string.Join("", hits.Select((p, i) =>
        {
            var label = ProductNameMatcher.RankLabel(p.Score, isTopHit: i == 0);
            var unitCode = unitCodes.GetValueOrDefault(p.DefaultUnitId, "");
            return ProductSearchOptionRenderer.RenderPickProductOption(
                p.Id.ToString(), p.Name, label,
                [
                    new ProductOptionField("track", p.TrackStock ? "true" : "false", "track"),
                    new ProductOptionField("default-unit", p.DefaultUnitId.ToString(), "defaultUnit"),
                    new ProductOptionField("default-unit-code", unitCode, "defaultUnitCode"),
                ]);
        }));
        return Content(html, "text/html");
    }

    // ── Recipe search for the "Include a recipe" affordance (recipe-composition.md D11) ──

    /// <summary>
    /// Returns JSON hits for the "Include a recipe" search — the household's non-archived recipes whose name
    /// matches <paramref name="q"/>, excluding the recipe currently being edited (self-inclusion, N2). Reuses
    /// the meal-planner recipe-search JSON pattern (a Recipes-scoped equivalent). Each hit carries the sub's
    /// id, name, and DefaultServings so the editor can seed the servings stepper's batch-fraction hint (D2).
    /// N4 at save remains authoritative — this exclusion is a client-side courtesy only.
    /// </summary>
    public async Task<IActionResult> OnGetSearchRecipesAsync(string? q, CancellationToken ct)
    {
        var query = (q ?? string.Empty).Trim();
        var all = await recipes.ListForBrowseAsync(ct);
        var hits = all
            .Where(r => Id is not { } selfId || r.Id.Value != selfId)
            .Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(r => new
            {
                id = r.Id.Value.ToString("D"),
                name = r.Name,
                defaultServings = r.DefaultServings,
            })
            .ToList();

        return new JsonResult(new { hits });
    }

    // ── AI tag suggestions (plantry-qll2.2) ──────────────────────────────────────

    /// <summary>
    /// Returns AI-proposed tag chips for the recipe currently being authored, given its chosen product
    /// ids and the tag names already applied in the editor (<paramref name="appliedTagNames"/> — existing
    /// chips AND accepted-but-unsaved new-tag chips, straight from the client's live Alpine state). Called
    /// by the editor once per create (client-triggered when the first ingredient is added — never on every
    /// save; the contradiction nudge, sibling bead qll2.3, covers edits).
    ///
    /// <para>All the read-side work — the household assistive-AI gate check, resolving product ids to
    /// ingredient names via the Catalog ACL, loading the tag vocabulary, and the untrusted LLM call —
    /// lives in <see cref="SuggestRecipeTags"/>, which also passes <paramref name="appliedTagNames"/>
    /// through so the model can avoid proposing a tag that's redundant with — a subset already implied
    /// by — an applied tag (plantry-crre). When the gate is off, no product resolves, or the suggester
    /// soft-fails, this returns <c>{"suggestions":[]}</c> and the editor renders nothing (no empty-state
    /// noise). A suggestion NEVER auto-applies: it becomes a tag only when the user taps its chip
    /// (Gate 5 / ADR-007).</para>
    /// </summary>
    public async Task<IActionResult> OnGetSuggestTagsAsync(Guid[] productIds, string[] appliedTagNames, CancellationToken ct)
    {
        var suggestions = await suggestRecipeTags.ExecuteAsync(productIds ?? [], appliedTagNames ?? [], ct);

        return new JsonResult(new
        {
            suggestions = suggestions.Select(s => new
            {
                name = s.Name,
                category = s.Category?.ToDbValue(),
                existingTagId = s.ExistingTagId?.ToString(),
                isNew = s.IsNew,
            }),
        });
    }

    // ── Conversion-gap check (live C10 pre-check) ────────────────────────────────

    /// <summary>
    /// Lightweight GET called by the Alpine <c>$watch</c> when the author picks a unit inside the add/edit
    /// ingredient sheet. Returns whether a conversion path exists from <paramref name="fromUnitId"/> to the
    /// product's default unit. Used to surface the in-sheet conversion prompt (C10 early UX) before the form
    /// is submitted — the authoritative R7 check at POST time is unchanged.
    ///
    /// <para>Same-dimension pairs (e.g. g → kg) resolve via universal conversions and return
    /// <c>needsConversion:false</c>. Cross-dimension or density gaps return <c>needsConversion:true</c>
    /// with the product's default unit id and code so the client can render the prompt correctly.</para>
    /// </summary>
    public async Task<IActionResult> OnGetCheckConversionAsync(Guid productId, Guid fromUnitId, CancellationToken ct)
    {
        var product = await products.FindAsync(productId, ct);
        if (product is null)
            return new JsonResult(new { needsConversion = false });

        var defaultUnitId = product.DefaultUnitId;

        // Same unit — no conversion needed (shortcut before calling the converter).
        if (fromUnitId == defaultUnitId)
            return new JsonResult(new { needsConversion = false });

        var path = await unitConverter.ConvertAsync(productId, 1m, fromUnitId, defaultUnitId, ct);
        if (path.IsSuccess)
            return new JsonResult(new { needsConversion = false });

        // No path — resolve the two axis-locked unit lists the four-field equation editor needs
        // (plantry-qno9). LEFT lists only units sharing the product stock/default-unit dimension;
        // RIGHT lists only units sharing the chosen recipe-line unit's dimension. Because the two
        // axes always differ (that is exactly why the prompt appears), the pair always bridges — a
        // same-dimension "nonsense" entry is impossible by construction, so no live guard is needed.
        var allUnits = await products.ListUnitsAsync(ct);
        var defaultUnit = allUnits.FirstOrDefault(u => u.Id == defaultUnitId);
        var recipeUnit  = allUnits.FirstOrDefault(u => u.Id == fromUnitId);

        // Robustness guard (plantry-obg3, split per plantry-hhy2): if either axis unit is unresolvable —
        // the product's DefaultUnitId is Guid.Empty or dangles outside the household unit list, or resolves
        // to a blank Code, and symmetrically for the chosen recipe unit — we cannot build a labelled
        // equation. Returning the pre-obg3 half-empty needsConversion:true payload rendered a blank unit
        // name in the "Plantry stocks X in ___" sentence AND an option-less LEFT dropdown. Return explicit
        // per-axis missing flags instead so the client shows the axis-appropriate human-readable message and
        // never mounts the four-field equation; the authoritative POST-time R7/AuthorRecipe check still
        // backstops a genuine missing path. This never returns needsConversion:false — that would silently
        // defer the failure. defaultUnitMissing is strictly the LEFT/stock axis (its original obg3 meaning);
        // recipeUnitMissing is the RIGHT/recipe axis. When both are missing the client shows only the stock
        // copy (it carries the actionable "set a default unit" remedy).
        var stockUnitMissing  = defaultUnit is null || string.IsNullOrWhiteSpace(defaultUnit.Code);
        var recipeUnitMissing = recipeUnit  is null || string.IsNullOrWhiteSpace(recipeUnit.Code);
        if (stockUnitMissing || recipeUnitMissing)
        {
            return new JsonResult(new
            {
                needsConversion = true,
                defaultUnitMissing = stockUnitMissing,
                recipeUnitMissing,
            });
        }

        // Both axes resolve to a labelled unit, so stockUnits always contains at least the default unit
        // and recipeUnits at least the chosen recipe unit — neither list can come back empty here (AC4).
        // The guard above returned whenever either was null, so both are non-null here; the split into
        // local bools loses the flow-narrowing the original combined `if (x is null || …)` gave us, so
        // re-bind through non-null locals to keep the compiler happy without a nullable-dereference warning.
        var resolvedDefaultUnit = defaultUnit!;
        var resolvedRecipeUnit  = recipeUnit!;
        var defaultUnitCode = resolvedDefaultUnit.Code;
        var stockUnits = allUnits
            .Where(u => u.Dimension == resolvedDefaultUnit.Dimension)
            .Select(u => new { id = u.Id.ToString(), code = u.Code, factorToBase = u.FactorToBase })
            .ToList();
        var recipeUnits = allUnits
            .Where(u => u.Dimension == resolvedRecipeUnit.Dimension)
            .Select(u => new { id = u.Id.ToString(), code = u.Code, factorToBase = u.FactorToBase })
            .ToList();

        return new JsonResult(new
        {
            needsConversion = true,
            defaultUnitId = defaultUnitId.ToString(),
            defaultUnitCode,
            stockUnits,
            recipeUnits,
        });
    }

    /// <summary>
    /// Product-less variant of <see cref="OnGetCheckConversionAsync"/> for the inline-create flow
    /// (plantry-dtr9). The create view mints a brand-new product that does not exist yet, so there is no
    /// <c>productId</c> to scope a conversion lookup — and a brand-new product carries zero
    /// <c>ProductConversion</c>s. The only thing that decides whether the author must supply a factor is
    /// therefore whether the chosen recipe-line unit shares the product's chosen default/stock unit
    /// dimension: same-dimension pairs (e.g. ml → tbsp, both Volume) bridge universally via
    /// <c>factor_to_base</c> and return <c>needsConversion:false</c>; cross-dimension pairs (e.g. ea → g)
    /// return <c>needsConversion:true</c> with the same axis-locked unit lists the existing-product handler
    /// builds, so the client renders the identical in-sheet four-field prompt BEFORE the product is minted
    /// (avoiding a save bounce that would orphan the just-created product). The authoritative R7 re-check at
    /// POST time is unchanged.
    /// </summary>
    public async Task<IActionResult> OnGetCheckConversionUnitsAsync(Guid defaultUnitId, Guid fromUnitId, CancellationToken ct)
    {
        // Same unit — no conversion needed.
        if (fromUnitId == defaultUnitId)
            return new JsonResult(new { needsConversion = false });

        var allUnits = await products.ListUnitsAsync(ct);
        var defaultUnit = allUnits.FirstOrDefault(u => u.Id == defaultUnitId);
        var recipeUnit = allUnits.FirstOrDefault(u => u.Id == fromUnitId);

        // Unresolvable unit id, or a same-dimension pair → no author factor required (same-dimension pairs
        // convert universally). Returning false on an unresolvable unit also keeps the prompt from rendering
        // blank; the authoritative R7 re-check at POST time still backstops a genuinely missing path.
        if (defaultUnit is null || recipeUnit is null || defaultUnit.Dimension == recipeUnit.Dimension)
            return new JsonResult(new { needsConversion = false });

        // Cross-dimension — build the two axis-locked lists the four-field equation editor needs (plantry-qno9).
        var stockUnits = allUnits
            .Where(u => u.Dimension == defaultUnit.Dimension)
            .Select(u => new { id = u.Id.ToString(), code = u.Code, factorToBase = u.FactorToBase })
            .ToList();
        var recipeUnits = allUnits
            .Where(u => u.Dimension == recipeUnit.Dimension)
            .Select(u => new { id = u.Id.ToString(), code = u.Code, factorToBase = u.FactorToBase })
            .ToList();

        return new JsonResult(new
        {
            needsConversion = true,
            defaultUnitId = defaultUnitId.ToString(),
            defaultUnitCode = defaultUnit.Code,
            stockUnits,
            recipeUnits,
        });
    }

    // ── POST — main save ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAsync(IFormFile? photo, CancellationToken ct)
    {
        await LoadReferenceDataAsync(ct);

        // ProductName and TagNames are not posted (display-only fields omitted from hidden inputs).
        // Repopulate them here so any Page() re-render has the ingredient names and tag chips intact.
        await RestoreTagNamesAsync(ct);

        // D13 — recompute includer info so the fixed-mode servings warning survives a failed-save re-render.
        if (Id is { } editIdForIncluders)
            await LoadIncluderInfoAsync(RecipeId.From(editIdForIncluders), ct);

        if (!ModelState.IsValid)
            return Page();

        // Build the ingredient lines from form input
        var lines = Input.Lines
            .Where(l => l.ProductId.HasValue || !string.IsNullOrWhiteSpace(l.NewStapleName))
            .Select(l =>
            {
                // plantry-qno9: the four-field in-sheet equation posts raw amounts + units on each side.
                // The server is authoritative for the factor: factor = rightAmount / leftAmount (decimal),
                // from = left unit, to = right unit. Amounts <= 0 (or missing) never produce a factor, so
                // no ProductConversion is written and AuthorRecipe's post-write re-check re-surfaces
                // NeedsConversion (no broken recipe saved). When the four-field values are absent (the
                // single-factor post-save backstop), fall back to the posted ConversionFactor with no
                // explicit from/to, and AuthorRecipe assumes recipeUnit→productDefault.
                var (convFactor, convFrom, convTo) = ResolveConversion(l);
                return new AuthorIngredientLine(
                    ProductId: l.ProductId,
                    Quantity: l.Quantity,
                    UnitId: l.UnitId,
                    GroupHeading: string.IsNullOrWhiteSpace(l.GroupHeading) ? null : l.GroupHeading.Trim(),
                    Ordinal: l.Ordinal,
                    NewStapleName: l.NewStapleName,
                    NewStapleDefaultUnitId: l.NewStapleDefaultUnitId,
                    ConversionFactor: convFactor,
                    NewIsTracked: l.NewIsTracked,
                    NewGroupId: l.NewGroupId,
                    NewGroupName: l.NewGroupName,
                    NewStapleCategoryId: l.NewStapleCategoryId,
                    ConversionFromUnitId: convFrom,
                    ConversionToUnitId: convTo);
            })
            .ToList();

        // Inclusion lines (recipe-composition.md §3, D1). Ordinal is shared with the ingredient lines;
        // AuthorRecipe canonicalises the union of both line types to a contiguous 0-based sequence. Blank
        // rows (no sub chosen) are dropped. Same-household / sub-existence / N2 / N4 are enforced server-side
        // by AuthorRecipe; N1 (Servings > 0) by RecipeLineSet.Create — surfaced here as a validation banner.
        var inclusions = Input.Inclusions
            .Where(i => i.SubRecipeId != Guid.Empty)
            .Select(i => new AuthorInclusionLine(
                SubRecipeId: i.SubRecipeId,
                Servings: i.Servings,
                GroupHeading: string.IsNullOrWhiteSpace(i.GroupHeading) ? null : i.GroupHeading.Trim(),
                Ordinal: i.Ordinal))
            .ToList();

        // R3′ — a recipe must carry at least one ingredient OR one inclusion (D3).
        if (lines.Count == 0 && inclusions.Count == 0)
        {
            SaveError = "A recipe must have at least one ingredient or included recipe.";
            RestoreLines();
            return Page();
        }

        // Mint any AI-suggested new-tag chips the user accepted (plantry-qll2.2). Minting happens here,
        // only once we are committing a valid recipe (past the ingredient guard) — a suggestion becomes a
        // tag solely through the user's tap (Gate 5). The freshly-minted ids join the closed-vocabulary
        // TagIds so AuthorRecipe resolves them exactly as picker-selected tags; its "unknown ids dropped,
        // never mints" contract is untouched.
        var mintedTagIds = await MintAcceptedNewTagsAsync(ct);

        var tagIds = (Input.TagIds ?? [])
            .Concat(mintedTagIds)
            .Distinct()
            .ToList();

        // Edit-moment AI conversion resolution (plantry-qll2.4): when the household's assistive-AI toggle is
        // on AND a conversion seeder is actually configured, a cross-dimension unit gap (weight-stocked
        // product used by volume) no longer blocks with the inline C10 prompt — the recipe saves WITH the
        // gap and an ai_suggested factor is seeded asynchronously (ADR-022). Both conditions are required:
        // with the toggle off, or on a keyless host where no seeder can run, we keep today's manual prompt
        // (acceptance criterion 4). The gate check is one cheap read per save; recipe saves are infrequent.
        var deferConversions = conversionSeedTrigger.Available && await aiGate.IsEnabledAsync(ct);

        var command = new AuthorRecipeCommand(
            RecipeId: Id.HasValue ? RecipeId.From(Id.Value) : null,
            Name: Input.Name?.Trim() ?? "",
            DefaultServings: Input.DefaultServings,
            Lines: lines,
            TagIds: tagIds,
            Source: string.IsNullOrWhiteSpace(Input.Source) ? null : Input.Source.Trim(),
            CookTimeMinutes: Input.CookTimeMinutes,
            Directions: string.IsNullOrWhiteSpace(Input.Directions) ? null : Input.Directions,
            ScaleMode: Input.ScaleMode,
            DeferMissingConversions: deferConversions,
            Inclusions: inclusions,
            // Yield-on-cook (plantry-854a): the product is auto-created from the recipe name (or the
            // existing declared product is reused on edit), so the UI supplies only quantity + unit.
            YieldEnabled: Input.YieldEnabled,
            YieldProductId: null,
            YieldQuantity: Input.YieldEnabled ? Input.YieldQuantity : null,
            YieldUnitId: Input.YieldEnabled ? Input.YieldUnitId : null);

        // Diet-tag nudge guard (plantry-qll2.3 / recipe-composition.md §8, D9): capture the recipe's EXPANDED
        // ProductId set BEFORE the save — direct ingredients plus every nested inclusion's products — for edits
        // only, so the post-save trigger can tell whether the effective ingredient set actually changed (an
        // effective-neutral edit must fire nothing; editing which recipes are included DOES change it). One
        // recursive repo walk, still no LLM and no name resolution. Create (J6) is owned by the qll2.2 tag chips,
        // never nudged.
        IReadOnlySet<Guid> previousProductIds = new HashSet<Guid>();
        if (Id is { } nudgeEditId)
        {
            var preExpanded = await recipeExpansion.ExpandedProductIdsAsync(RecipeId.From(nudgeEditId), ct);
            if (preExpanded.IsSuccess)
                previousProductIds = preExpanded.Value;
        }

        var result = await authorRecipe.ExecuteAsync(command, ct);

        switch (result)
        {
            case AuthorRecipeResult.Saved saved:
                // Photo upload — applied after the recipe is saved so we have the aggregate id.
                if (photo is { Length: > 0 })
                    await ApplyPhotoAsync(saved.RecipeId, photo, ct);

                // plantry-qll2.4: the recipe saved with one or more cross-dimension unit gaps (deferral was
                // on). Fire a fire-and-forget async seed of an ai_suggested conversion for each — the user
                // is never prompted and this never delays the redirect.
                if (saved.DeferredConversions.Count > 0)
                    await EnqueueConversionSeedsAsync(saved.DeferredConversions, ct);

                // Diet-tag nudge (plantry-qll2.3): on an edit whose ingredient set changed to a not-yet-reconciled
                // set on a Diet-tagged recipe, flag the post-save Details landing to run the deferred contradiction
                // check by tagging the redirect with ?dietNudge=true. The cheap guard runs here (no LLM); the gate +
                // LLM run later on the Details landing, only if the flag is present — so most saves cost nothing and
                // the AI stays confined to the edit moment (a plain recipe view carries no flag, so never a sweep).
                var offerNudge = !IsCreate
                    && await dietTagNudge.ShouldOfferAfterSaveAsync(saved.RecipeId, previousProductIds, ct);

                // Reverse ripple (recipe-composition.md §8 / D10): saving this recipe changes the EXPANDED product
                // set of any recipe that INCLUDES it — with no parent save to fire the direct nudge. Run the cheap,
                // no-LLM guard for each transitively-including diet-tagged parent and carry the unreconciled ones to
                // this recipe's save landing, where a per-parent deferred nudge names the conflict ("may conflict with
                // 'Vegan' on Nachos"). Skipped on create (a brand-new recipe has no includers); does no expansion/LLM
                // work beyond the includers lookup when nothing diet-tagged includes this recipe (criterion 4).
                var rippleParentIds = IsCreate
                    ? []
                    : await dietTagNudge.IncludersNeedingRippleNudgeAsync(saved.RecipeId, ct);

                var detailsRoute = new RouteValueDictionary { ["id"] = saved.RecipeId.Value };
                if (offerNudge)
                    detailsRoute["dietNudge"] = true;
                if (rippleParentIds.Count > 0)
                    detailsRoute["rippleParents"] = string.Join(",", rippleParentIds.Select(p => p.Value));
                return RedirectToPage("./Details", detailsRoute);

            case AuthorRecipeResult.NeedsConversion needs:
                // Surface the inline ProductConversion form on the affected rows (C10).
                SaveError = "Some ingredient units need a conversion factor — please fill in the highlighted rows.";
                ConversionRequiredOrdinals = needs.Conversions.Select(c => c.Ordinal).ToList();
                // Annotate the affected input rows so the view can show the conversion field
                foreach (var needed in needs.Conversions)
                {
                    var row = Input.Lines.FirstOrDefault(l => l.Ordinal == needed.Ordinal);
                    if (row is not null)
                    {
                        row.NeedsConversion = true;
                        row.ConversionFromUnitId = needed.FromUnitId;
                        row.ConversionToUnitId = needed.ToUnitId;
                        // plantry-dnbe: extend obg3's axis-resolvability guard (OnGetCheckConversionAsync,
                        // above) to this authoritative POST-bounce path. ConversionNeeded.ToUnitId is the
                        // product's DefaultUnitId (a DM-3 soft reference with no FK) and FromUnitId is the
                        // chosen recipe-line unit; either can dangle outside the household unit list or carry
                        // a blank Code. Without this, the row re-renders with defaultUnitMissing=false and a
                        // blank defaultUnitCode, reproducing obg3's blank-sentence/option-less-dropdown defect
                        // via the POST render instead of the AJAX path. Set the same per-axis flags obg3 sets
                        // so the FIRST render carries the friendly missing-unit message, not the client's
                        // after-the-fact maybeHydrateRowConversion fetch. defaultUnitMissing is the LEFT/stock
                        // axis (the ToUnitId = product default), recipeUnitMissing the RIGHT/recipe axis.
                        row.DefaultUnitMissing = IsUnitUnresolvable(needed.ToUnitId);
                        row.RecipeUnitMissing = IsUnitUnresolvable(needed.FromUnitId);
                    }
                }
                RestoreLines();
                return Page();

            case AuthorRecipeResult.Invalid inv:
                SaveError = inv.Error.Description;
                RestoreLines();
                return Page();

            default:
                SaveError = "Unexpected error — please try again.";
                RestoreLines();
                return Page();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// plantry-dnbe: is <paramref name="unitId"/> unresolvable against the household unit list — the same
    /// axis-resolvability predicate obg3 applies in <see cref="OnGetCheckConversionAsync"/>
    /// (<c>unit is null || string.IsNullOrWhiteSpace(unit.Code)</c>), evaluated here against
    /// <see cref="UnitOptions"/> (loaded from <see cref="ICatalogProductReader.ListUnitsAsync"/> in
    /// <see cref="LoadReferenceDataAsync"/>, its <c>Text</c> being the unit Code). Returns true when the id
    /// dangles outside the list (unit deleted, or an unset <c>Guid.Empty</c> default) or resolves to a
    /// blank Code — the DM-3 soft-reference conditions that render a blank conversion prompt if unguarded.
    /// </summary>
    private bool IsUnitUnresolvable(Guid unitId)
    {
        var option = UnitOptions.FirstOrDefault(u => u.Value == unitId.ToString());
        return option is null || string.IsNullOrWhiteSpace(option.Text);
    }

    /// <summary>
    /// Server-authoritative resolution of a row's conversion (plantry-qno9). Prefers the four-field
    /// equation (left/right amounts + units): computes <c>factor = rightAmount / leftAmount</c> with
    /// <c>from = left unit</c>, <c>to = right unit</c>, and returns no factor when either amount is
    /// missing or ≤ 0 (so nothing is written and the post-write re-check re-surfaces NeedsConversion).
    /// Falls back to the single posted <see cref="IngredientRowInput.ConversionFactor"/> (the post-save
    /// row-level backstop) with no explicit from/to, letting AuthorRecipe assume recipeUnit→productDefault.
    /// </summary>
    private static (decimal? Factor, Guid? From, Guid? To) ResolveConversion(IngredientRowInput l)
    {
        if (l.ConversionLeftUnitId is { } from && l.ConversionRightUnitId is { } to
            && l.ConversionLeftAmount is { } left && l.ConversionRightAmount is { } right
            && left > 0 && right > 0)
        {
            return (right / left, from, to);
        }

        return (l.ConversionFactor, null, null);
    }

    /// <summary>
    /// Mints the AI-suggested new tags the author accepted (the dashed <c>.ai-chip--new</c> chips,
    /// plantry-qll2.2) and returns their resolved household tag ids. Each proposed name is created via
    /// <see cref="ManageTagsService.CreateAsync"/> (which enforces per-household name uniqueness — an
    /// already-existing active name is a harmless <c>Conflict</c>). We then re-read the active vocabulary
    /// once and resolve every proposed name (whether just-minted or already active) to its id, so callers
    /// get back exactly the ids to apply. A name matching only an <b>archived</b> tag is absent from the
    /// active set and silently dropped — consistent with AuthorRecipe's "unknown ids dropped" contract.
    /// The AI-proposed category (cosmetic only) is applied when it maps to a known <see cref="TagCategory"/>.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> MintAcceptedNewTagsAsync(CancellationToken ct)
    {
        var newTags = (Input.NewTags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToList();
        if (newTags.Count == 0)
            return [];

        foreach (var nt in newTags)
            await manageTags.CreateAsync(nt.Name!.Trim(), ParseTagCategory(nt.Category), ct);

        var active = await tags.ListAllAsync(activeOnly: true, ct);
        var byName = active.ToDictionary(t => t.Name, t => t.Id.Value, StringComparer.OrdinalIgnoreCase);

        return newTags
            .Select(t => byName.TryGetValue(t.Name!.Trim(), out var id) ? (Guid?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>Safely maps a posted category label to <see cref="TagCategory"/>; unknown/blank ⇒ null (cosmetic only).</summary>
    private static TagCategory? ParseTagCategory(string? label) => label switch
    {
        "Diet" => TagCategory.Diet,
        "Protein" => TagCategory.Protein,
        "Flavor" => TagCategory.Flavor,
        "Cuisine" => TagCategory.Cuisine,
        _ => null,
    };

    /// <summary>
    /// Loads the direct-includer count and names for the recipe being edited (D13 fixed-mode warning).
    /// Household-scoped by the RLS query filter; a no-op set of empties when nothing includes the recipe.
    /// </summary>
    private async Task LoadIncluderInfoAsync(RecipeId recipeId, CancellationToken ct)
    {
        var includerIds = await recipes.GetIncluderIdsAsync(recipeId, transitive: false, ct);
        if (includerIds.Count == 0)
        {
            IncludedByCount = 0;
            IncludedByNames = [];
            return;
        }

        IncludedByCount = includerIds.Count;
        var names = await recipes.GetRecipeNamesByIdAsync(includerIds.ToList(), ct);
        IncludedByNames = includerIds
            .Select(rid => names.GetValueOrDefault(rid) ?? "(a recipe)")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task LoadReferenceDataAsync(CancellationToken ct)
    {
        // Unit list via the anti-corruption port — never through IUnitRepository directly (Gate 2).
        var unitOptions = await products.ListUnitsAsync(ct);
        UnitOptions = unitOptions
            .Select(u => new SelectListItem(u.Code, u.Id.ToString()))
            .ToList();

        // Active tag options — activeOnly:true so archived tags are excluded from the picker dropdown.
        // Tags are a small set (dozens, not thousands) — list all and filter client-side in Alpine.
        var activeTags = await tags.ListAllAsync(activeOnly: true, ct);
        TagOptions = activeTags
            .Select(t => new SelectListItem(t.Name, t.Id.Value.ToString()))
            .ToList();

        // Load group options for the create-view Group combobox (plantry-orix).
        // Groups are active products with HasVariants = true (IsParent). Filtered client-side in Alpine.
        // Loaded via ICatalogProductReader.ListGroupsAsync (the anti-corruption port — Gate 2).
        var groupOptions = await products.ListGroupsAsync(ct);
        GroupOptions = groupOptions
            .Select(g => new GroupOption(g.Id.ToString(), g.Name))
            .ToList();

        // Load category options for the Defaults collapsible in the create view (plantry-orix).
        // Loaded via ICatalogProductReader.ListCategoriesAsync (the anti-corruption port — Gate 2).
        var categoryOptions = await products.ListCategoriesAsync(ct);
        CategoryOptions = categoryOptions
            .Select(c => new SelectListItem(c.Name, c.Id.ToString()))
            .ToList();
    }

    /// <summary>
    /// Resolves the deferred cross-dimension unit gaps (plantry-qll2.4) into <see cref="ConversionSeedRequest"/>s
    /// — product name + unit codes for the LLM prompt — and hands them to the async seed trigger. Only
    /// genuine cross-dimension gaps are enqueued: a same-dimension pair (which would resolve via universal
    /// conversions and never reach here) is defensively dropped so a nonsensical density factor is never
    /// requested. No-op when there is no household in context.
    /// </summary>
    private async Task EnqueueConversionSeedsAsync(
        IReadOnlyList<ConversionNeeded> deferred, CancellationToken ct)
    {
        if (tenant.HouseholdId is not { } householdGuid || deferred.Count == 0)
            return;

        var productIds = deferred.Select(d => d.ProductId).Distinct().ToList();
        var productLookup = await products.ResolveSummariesAsync(productIds, ct);

        // ListUnitsAsync carries each unit's Dimension + Code — used both to build the prompt and to keep
        // only true cross-dimension gaps.
        var units = await products.ListUnitsAsync(ct);
        var unitById = units.ToDictionary(u => u.Id);

        var requests = new List<ConversionSeedRequest>(deferred.Count);
        foreach (var gap in deferred)
        {
            if (!productLookup.TryGetValue(gap.ProductId, out var product)) continue;
            if (!unitById.TryGetValue(gap.FromUnitId, out var fromUnit)) continue;
            if (!unitById.TryGetValue(gap.ToUnitId, out var toUnit)) continue;

            // Cross-dimension only — a same-dimension pair never needs a product-specific density factor.
            if (!string.IsNullOrEmpty(fromUnit.Dimension)
                && string.Equals(fromUnit.Dimension, toUnit.Dimension, StringComparison.Ordinal))
                continue;

            requests.Add(new ConversionSeedRequest(
                gap.ProductId, product.Name, gap.FromUnitId, fromUnit.Code, gap.ToUnitId, toUnit.Code));
        }

        await conversionSeedTrigger.EnqueueAsync(HouseholdId.From(householdGuid), requests, ct);
    }

    private async Task ApplyPhotoAsync(RecipeId recipeId, IFormFile photo, CancellationToken ct)
    {
        // Load the recipe, apply photo, save.
        var recipe = await recipes.GetByIdAsync(recipeId, ct);
        if (recipe is null) return;

        // CopyToAsync guarantees the full upload is read regardless of the number of internal
        // read calls — avoids the partial-read bug of a single ReadAsync into a pre-sized buffer.
        using var ms = new MemoryStream();
        await photo.CopyToAsync(ms, ct);
        var buffer = ms.ToArray();

        recipe.SetPhoto(buffer, photo.ContentType, sha256: null, clock);
        await recipes.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Orders ingredients into the canonical section layout the sectioned editor and the Details page
    /// share (plantry-vff8): ungrouped ingredients first, then each distinct <c>GroupHeading</c> in
    /// first-appearance order (mirroring Details' <c>OrderBy(Ordinal).GroupBy(GroupHeading)</c>),
    /// preserving each section's members in ordinal order. The caller reassigns contiguous ordinals
    /// from the returned order, so the editor posts exactly the order Details renders.
    /// </summary>
    private static List<Ingredient> CanonicaliseSectionOrder(IEnumerable<Ingredient> ingredients)
    {
        var ordered = ingredients.OrderBy(i => i.Ordinal).ToList();

        var headingOrder = new List<string>();
        foreach (var ing in ordered)
        {
            if (!string.IsNullOrWhiteSpace(ing.GroupHeading) && !headingOrder.Contains(ing.GroupHeading))
                headingOrder.Add(ing.GroupHeading);
        }

        var result = new List<Ingredient>(ordered.Count);
        result.AddRange(ordered.Where(i => string.IsNullOrWhiteSpace(i.GroupHeading)));
        foreach (var heading in headingOrder)
            result.AddRange(ordered.Where(i => i.GroupHeading == heading));

        return result;
    }

    private void RestoreLines()
    {
        // Drop blank rows (no product chosen and no staple name) so the read-only ingredient list
        // doesn't render ghost entries after a failed save. Rows flagged for conversion keep their
        // ProductId, so the NeedsConversion annotations set above survive this filter.
        Input.Lines = Input.Lines
            .Where(l => l.ProductId.HasValue || !string.IsNullOrWhiteSpace(l.NewStapleName))
            .ToList();

        // Drop blank inclusion rows (no sub chosen) so a failed-save re-render carries no ghost entries.
        Input.Inclusions = Input.Inclusions
            .Where(i => i.SubRecipeId != Guid.Empty)
            .ToList();
    }

    /// <summary>
    /// Repopulates <see cref="RecipeEditInput.TagNames"/> from the already-loaded <see cref="TagOptions"/>
    /// after a POST, falling back to <see cref="ITagRepository.ResolveNamesAsync"/> for any archived
    /// applied tags not present in the active-only <see cref="TagOptions"/> set.
    ///
    /// <para>Tag names are display-only and are not posted with the form (only <see cref="RecipeEditInput.TagIds"/>
    /// are). Without this, the re-rendered page serialises an empty TagNames list and the Alpine chip
    /// state loses all tag display labels, blanking the chips in the editor. Archived applied tags
    /// would degrade to a truncated GUID stub if resolved only from the active-only picker list.</para>
    ///
    /// <para>Mirrors the GET pre-population at lines 100–106 which uses <see cref="ITagRepository.ResolveNamesAsync"/>
    /// (archived tags included) for the same id→name resolution.</para>
    /// </summary>
    private async Task RestoreTagNamesAsync(CancellationToken ct)
    {
        var nameDict = TagOptions.ToDictionary(o => Guid.Parse(o.Value), o => o.Text);

        // Collect any applied tag ids that are not in the active-only TagOptions (i.e. archived tags).
        var missingIds = Input.TagIds
            .Where(id => !nameDict.ContainsKey(id))
            .Select(id => new TagId(id))
            .ToList();

        if (missingIds.Count > 0)
        {
            // ResolveNamesAsync includes archived tags — mirrors the GET pre-population path.
            var archivedNames = await tags.ResolveNamesAsync(missingIds, ct);
            foreach (var (tagId, name) in archivedNames)
                nameDict[tagId.Value] = name;
        }

        Input.TagNames = Input.TagIds
            .Select(id => nameDict.GetValueOrDefault(id) ?? id.ToString("N")[..8])
            .ToList();
    }
}

// ── Input models ─────────────────────────────────────────────────────────────────

/// <summary>Top-level form binding for the recipe editor.</summary>
public sealed class RecipeEditInput
{
    public string? Name { get; set; }
    public int DefaultServings { get; set; } = 1;
    public int? CookTimeMinutes { get; set; }
    public string? Source { get; set; }
    public string? Directions { get; set; }

    // ── Yield-on-cook (plantry-854a, recipe-composition.md §9) ───────────────────
    /// <summary>Whether the recipe declares a yield (stored leftover / prepped stock on cook).</summary>
    public bool YieldEnabled { get; set; }

    /// <summary>Declared yield quantity for the default servings (&gt; 0 when enabled).</summary>
    public decimal? YieldQuantity { get; set; }

    /// <summary>Unit of the yield quantity — a servings-like count unit by default.</summary>
    public Guid? YieldUnitId { get; set; }

    /// <summary>
    /// Selected tag ids — posted by the closed-vocabulary picker as hidden inputs (one per chip).
    /// Resolved by <see cref="AuthorRecipe"/> to existing household <see cref="Plantry.Recipes.Domain.Tag"/>s;
    /// unknown or foreign ids are dropped silently (no minting).
    /// </summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>
    /// Tag display names parallel to <see cref="TagIds"/> — used only on the GET path to seed the
    /// Alpine <c>tags</c> state with <c>{id, name}</c> objects. Not posted on submit (only
    /// <see cref="TagIds"/> are posted). Populated by the page model's GET pre-population code.
    /// </summary>
    public List<string> TagNames { get; set; } = [];

    /// <summary>
    /// AI-suggested NEW tags the author accepted from the suggestion row (plantry-qll2.2) — tags not in the
    /// household vocabulary, posted by name (+ optional cosmetic category) as indexed hidden inputs. The page
    /// model mints these on save (<see cref="EditModel.MintAcceptedNewTagsAsync"/>) and folds their ids into
    /// the submitted tag set; existing-vocabulary suggestions ride in <see cref="TagIds"/> like any picker
    /// selection. Empty unless the user tapped a dashed new-tag chip.
    /// </summary>
    public List<NewTagInput> NewTags { get; set; } = [];

    /// <summary>
    /// Scale mode for the J7 servings change — Proportional scales ingredient quantities;
    /// Keep preserves them. Only meaningful when DefaultServings differs from the recipe's
    /// current value on an edit.
    /// </summary>
    public ScaleMode ScaleMode { get; set; } = ScaleMode.Keep;

    public List<IngredientRowInput> Lines { get; set; } = [];

    /// <summary>
    /// Inclusion rows (recipe-composition.md §3 / D1) — "N servings of a sub-recipe" line items. Posted by
    /// the editor's "Include a recipe" affordance as indexed hidden inputs (<c>Input.Inclusions[j].*</c>),
    /// carrying the shared-space <see cref="InclusionRowInput.Ordinal"/>. The page model builds
    /// <see cref="AuthorInclusionLine"/>s from these; N1/N2/N4 are enforced downstream.
    /// </summary>
    public List<InclusionRowInput> Inclusions { get; set; } = [];
}

/// <summary>
/// One inclusion row in the editor (recipe-composition.md §3). Carries the chosen sub-recipe id + its
/// servings, plus display-only fields (<see cref="SubName"/>, <see cref="SubDefaultServings"/>) that
/// round-trip via hidden inputs so a failed-save re-render keeps the row's title and batch-fraction hint
/// intact. <see cref="Ordinal"/> is in the shared ordinal space with <see cref="IngredientRowInput"/>.
/// </summary>
public sealed class InclusionRowInput
{
    public int Ordinal { get; set; }

    /// <summary>The included (sub) recipe's id.</summary>
    public Guid SubRecipeId { get; set; }

    /// <summary>Sub-recipe display name — display-only, posted via a hidden input so it survives re-renders.</summary>
    public string? SubName { get; set; }

    /// <summary>Servings of the sub-recipe (&gt; 0, N1). Decimal so half/quarter batches are expressible.</summary>
    public decimal Servings { get; set; } = 1m;

    /// <summary>The sub-recipe's DefaultServings — display-only, drives the "· ½ batch" hint (D2).</summary>
    public int SubDefaultServings { get; set; } = 1;

    /// <summary>Optional section heading, shared with the ingredient group-heading convention.</summary>
    public string? GroupHeading { get; set; }
}

/// <summary>
/// One accepted AI-suggested new tag (plantry-qll2.2) — a tag name not in the household vocabulary that the
/// author confirmed with a tap, to be minted on save. <see cref="Category"/> carries the model's proposed
/// cosmetic grouping (Diet/Protein/Flavor/Cuisine) or null.
/// </summary>
public sealed class NewTagInput
{
    public string? Name { get; set; }
    public string? Category { get; set; }
}

/// <summary>
/// One ingredient row in the editor. Carries either a chosen ProductId (product search/select)
/// or an inline create request. Two inline-create flavours:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Untracked staple</b> (C12): <see cref="NewIsTracked"/> = false.
///       <see cref="NewStapleName"/> + <see cref="NewStapleDefaultUnitId"/> posted from hidden inputs.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Tracked product</b> (plantry-orix): <see cref="NewIsTracked"/> = true.
///       Additional fields <see cref="NewGroupId"/>, <see cref="NewGroupName"/>,
///       <see cref="NewStapleCategoryId"/> drive group-aware routing in <see cref="AuthorRecipe"/>.
///     </description>
///   </item>
/// </list>
/// NeedsConversion and the two unit ids are set by the page model on a NeedsConversion outcome so
/// the view can display the inline conversion form (C10).
/// </summary>
public sealed class IngredientRowInput
{
    public int Ordinal { get; set; }

    // ── Product ────────────────────────────────────────────────────────────────────
    public Guid? ProductId { get; set; }
    /// <summary>
    /// Product display name — posted via a hidden input so it survives POST re-renders (validation
    /// failure re-render preserves ingredient row names). Populated on GET by the page model's
    /// pre-population loop, and re-posted by the hidden <c>Input.Lines[n].ProductName</c> inputs
    /// in the Alpine <c>x-for</c> template so <c>row.productName</c> remains intact client-side.
    /// </summary>
    public string? ProductName { get; set; }

    // ── Inline create (C12 untracked / plantry-orix tracked) ──────────────────────
    public string? NewStapleName { get; set; }
    public Guid? NewStapleDefaultUnitId { get; set; }

    /// <summary>
    /// When true, the inline create path mints a tracked product (track_stock = true) via the
    /// group-aware create paths in <see cref="AuthorRecipe"/> (plantry-orix). When false (default),
    /// the C12 untracked-staple path is used.
    /// </summary>
    public bool NewIsTracked { get; set; }

    /// <summary>
    /// For tracked-product create: the existing group product's id to join as a variant (Path A in
    /// <see cref="AuthorRecipe"/>). Empty string or null when not joining an existing group.
    /// </summary>
    public string? NewGroupId { get; set; }

    /// <summary>
    /// For tracked-product create: the name of a new group to create together with the first variant
    /// (Path B in <see cref="AuthorRecipe"/>). Empty string or null when not creating a new group.
    /// </summary>
    public string? NewGroupName { get; set; }

    /// <summary>
    /// For tracked-product create: the optional category id chosen in the Defaults collapsible
    /// (plantry-y53t). Empty Guid or null when no category is selected.
    /// </summary>
    public Guid? NewStapleCategoryId { get; set; }

    // ── Quantity / unit ────────────────────────────────────────────────────────────
    public decimal? Quantity { get; set; }
    public Guid? UnitId { get; set; }
    /// <summary>Display-only — the unit code string (e.g. "g") for pre-populating the select label on edit.</summary>
    public string? UnitCode { get; set; }

    /// <summary>
    /// The product's real default (stock) unit id, hydrated on GET from the Catalog batch lookup
    /// (plantry-obg3). Seeds the landed-row conversion ask heading and the client's same-unit
    /// short-circuit BEFORE the lazy hydration fetch runs. Not posted (no hidden input) — it is
    /// recomputed on the next GET; a NeedsConversion POST bounce re-derives the row default from
    /// <see cref="ConversionToUnitId"/> instead (set to the needed target unit in OnPostAsync).
    /// </summary>
    public Guid DefaultUnitId { get; set; }

    /// <summary>
    /// The product's live <c>track_stock = false</c> state, hydrated on GET from the resolved Catalog
    /// summary (plantry-429l — previously hard-coded false, a latent hydration lie). Posted as a hidden
    /// input so it round-trips through a failed-save re-render; display-only (the server re-resolves
    /// <c>TrackStock</c> from the product for the R5 decision, never trusting this client value). The
    /// editor derives the "needs a quantity and unit" warning reactively from this plus the row's qty/unit,
    /// so it self-clears the moment the author supplies the missing value.
    /// </summary>
    public bool IsUntracked { get; set; }

    // ── Optional group heading ─────────────────────────────────────────────────────
    public string? GroupHeading { get; set; }

    // ── Inline conversion form (C10 — set on NeedsConversion outcome) ─────────────
    public bool NeedsConversion { get; set; }
    public Guid ConversionFromUnitId { get; set; }
    public Guid ConversionToUnitId { get; set; }
    public decimal? ConversionFactor { get; set; }

    /// <summary>
    /// plantry-dnbe: the POST-bounce counterpart of the AJAX handler's <c>defaultUnitMissing</c> /
    /// <c>recipeUnitMissing</c> flags (plantry-obg3 / plantry-hhy2). Set true by the page model's
    /// <see cref="AuthorRecipeResult.NeedsConversion"/> case when the row's conversion target axis is
    /// unresolvable — the product's stock/default unit (<see cref="DefaultUnitMissing"/>, the
    /// <c>ConversionToUnitId</c> = <c>Product.DefaultUnitId</c> soft reference, DM-3) or the chosen
    /// recipe-line unit (<see cref="RecipeUnitMissing"/>, the <c>ConversionFromUnitId</c>) dangles outside
    /// the household unit list or resolves to a blank Code. Seeds the landed-row conversion block's
    /// <c>defaultUnitMissing</c>/<c>recipeUnitMissing</c> Alpine flags on the very first render after a
    /// save bounce, so it shows the friendly "has no stock unit set" message instead of a blank unit
    /// sentence + option-less dropdown — WITHOUT waiting for the client's <c>maybeHydrateRowConversion</c>
    /// AJAX rehydration to correct it after the fact. Not posted (no hidden input) — recomputed on the
    /// next bounce; default false on the GET path so the serialised row JSON is byte-identical there.
    /// </summary>
    public bool DefaultUnitMissing { get; set; }

    /// <summary>plantry-dnbe: RIGHT/recipe-axis counterpart of <see cref="DefaultUnitMissing"/> — see its docs.</summary>
    public bool RecipeUnitMissing { get; set; }

    // ── Four-field equation editor (plantry-qno9) ─────────────────────────────────
    // The in-sheet prompt lets the author state the cross-measure fact against ANY unit pair
    // ("1 kg = 8 cups"): LEFT amount+unit (product stock dimension) = RIGHT amount+unit (recipe-line
    // dimension). The server computes factor = right/left, from = left unit, to = right unit
    // (see ResolveConversion). These round-trip via hidden inputs so a failed-save re-render keeps them.
    public decimal? ConversionLeftAmount { get; set; }
    public Guid? ConversionLeftUnitId { get; set; }
    public decimal? ConversionRightAmount { get; set; }
    public Guid? ConversionRightUnitId { get; set; }
}
