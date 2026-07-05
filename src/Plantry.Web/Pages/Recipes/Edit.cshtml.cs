using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Pages.Shared;

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
    IUnitConverter unitConverter) : PageModel
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

            // Canonicalise ingredient order to match the sectioned editor + Details render
            // (plantry-vff8): ungrouped ingredients first, then each GroupHeading in first-appearance
            // order (preserving ordinal order within a section), then reassign contiguous ordinals.
            // Doing this on load means even a no-change save reconciles a legacy recipe's stored order
            // with the editor's Ungrouped-first layout — closing the editor-vs-detail snap-back.
            var canonicalIngredients = CanonicaliseSectionOrder(recipe.Ingredients);

            // plantry-429l: a line authored while its product was untracked (null qty/unit legal) whose
            // product was LATER flipped tracked (Product.SetTrackStock) is retroactively condemned by R5
            // on the next save. Detect such "needs qty/unit" rows at load so the editor can flag them and
            // prefill the missing unit, rather than dead-ending on an opaque global save error. A row is
            // flagged when the product is live-tracked AND the stored qty or unit is null. The product's
            // default unit (for the prefill) is not carried on the summary, so resolve it via FindAsync for
            // the flagged products only (rare — requires a deliberate untracked→tracked flip).
            var flaggedDefaultUnits = new Dictionary<Guid, Guid>();
            foreach (var ing in canonicalIngredients)
            {
                if (flaggedDefaultUnits.ContainsKey(ing.ProductId)) continue;
                var isTracked = productLookup.TryGetValue(ing.ProductId, out var s) && s.TrackStock;
                if (isTracked && (ing.Quantity is null || ing.UnitId is null))
                {
                    var full = await products.FindAsync(ing.ProductId, ct);
                    if (full is not null)
                        flaggedDefaultUnits[ing.ProductId] = full.DefaultUnitId;
                }
            }

            Input.Lines = canonicalIngredients
                .Select((ing, idx) =>
                {
                    productLookup.TryGetValue(ing.ProductId, out var p);
                    var isTracked = p?.TrackStock ?? false;
                    var needsQtyUnit = isTracked && (ing.Quantity is null || ing.UnitId is null);

                    // Part A (plantry-429l): prefill the unit for a flagged row that has no unit, using the
                    // product's default unit. The user then only supplies a quantity. Quantity is never
                    // prefilled (no sane default) — the null qty keeps the row flagged until the user acts.
                    var unitId = ing.UnitId;
                    if (needsQtyUnit && unitId is null
                        && flaggedDefaultUnits.TryGetValue(ing.ProductId, out var defUnit) && defUnit != Guid.Empty)
                        unitId = defUnit;

                    var unitCode = unitId.HasValue ? unitCodeLookup.GetValueOrDefault(unitId.Value) : null;
                    return new IngredientRowInput
                    {
                        Ordinal = idx,
                        ProductId = ing.ProductId,
                        ProductName = p?.Name ?? "",
                        Quantity = ing.Quantity,
                        UnitId = unitId,
                        UnitCode = unitCode,
                        GroupHeading = ing.GroupHeading,
                        IsUntracked = !isTracked,
                    };
                }).ToList();

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
        var enc = HtmlEncoder.Default;

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
            return $$"""<li role="option" data-value="{{p.Id}}" data-name="{{enc.Encode(p.Name)}}" data-track="{{(p.TrackStock ? "true" : "false")}}" data-default-unit="{{p.DefaultUnitId}}" data-default-unit-code="{{enc.Encode(unitCode)}}" @click="query = $el.dataset.name; open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.dataset.name, track: $el.dataset.track, defaultUnit: $el.dataset.defaultUnit, defaultUnitCode: $el.dataset.defaultUnitCode})">{{enc.Encode(p.Name)}}<span class="rk">{{enc.Encode(label)}}</span></li>""";
        }));
        return Content(html, "text/html");
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
        var defaultUnitCode = defaultUnit?.Code ?? "";

        var stockUnits = allUnits
            .Where(u => defaultUnit is not null && u.Dimension == defaultUnit.Dimension)
            .Select(u => new { id = u.Id.ToString(), code = u.Code, factorToBase = u.FactorToBase })
            .ToList();
        var recipeUnits = allUnits
            .Where(u => recipeUnit is not null && u.Dimension == recipeUnit.Dimension)
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

    // ── POST — main save ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAsync(IFormFile? photo, CancellationToken ct)
    {
        await LoadReferenceDataAsync(ct);

        // ProductName and TagNames are not posted (display-only fields omitted from hidden inputs).
        // Repopulate them here so any Page() re-render has the ingredient names and tag chips intact.
        await RestoreTagNamesAsync(ct);

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

        if (lines.Count == 0)
        {
            SaveError = "A recipe must have at least one ingredient.";
            RestoreLines();
            return Page();
        }

        var tagIds = (Input.TagIds ?? [])
            .Distinct()
            .ToList();

        var command = new AuthorRecipeCommand(
            RecipeId: Id.HasValue ? RecipeId.From(Id.Value) : null,
            Name: Input.Name?.Trim() ?? "",
            DefaultServings: Input.DefaultServings,
            Lines: lines,
            TagIds: tagIds,
            Source: string.IsNullOrWhiteSpace(Input.Source) ? null : Input.Source.Trim(),
            CookTimeMinutes: Input.CookTimeMinutes,
            Directions: string.IsNullOrWhiteSpace(Input.Directions) ? null : Input.Directions,
            ScaleMode: Input.ScaleMode);

        var result = await authorRecipe.ExecuteAsync(command, ct);

        switch (result)
        {
            case AuthorRecipeResult.Saved saved:
                // Photo upload — applied after the recipe is saved so we have the aggregate id.
                if (photo is { Length: > 0 })
                    await ApplyPhotoAsync(saved.RecipeId, photo, ct);
                return RedirectToPage("./Details", new { id = saved.RecipeId.Value });

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
    /// Scale mode for the J7 servings change — Proportional scales ingredient quantities;
    /// Keep preserves them. Only meaningful when DefaultServings differs from the recipe's
    /// current value on an edit.
    /// </summary>
    public ScaleMode ScaleMode { get; set; } = ScaleMode.Keep;

    public List<IngredientRowInput> Lines { get; set; } = [];
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
