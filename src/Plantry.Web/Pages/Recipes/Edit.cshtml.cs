using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

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
/// <para>All Catalog access flows through <see cref="ICatalogProductReader"/> (the anti-corruption port) —
/// this page never touches Catalog repositories directly (Gate 2).</para>
/// </summary>
[Authorize]
public sealed class EditModel(
    IRecipeRepository recipes,
    ITagRepository tags,
    ICatalogProductReader products,
    IClock clock,
    AuthorRecipe authorRecipe) : PageModel
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

    // ── Reference data for dropdowns ─────────────────────────────────────────────

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> TagOptions { get; private set; } = [];

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

            // Pre-populate tags — resolve their names for the tag chip display
            var tagIds = recipe.Tags.Select(rt => rt.TagId).ToList();
            var tagNameLookup = await tags.ResolveNamesAsync(tagIds, ct);
            Input.Tags = recipe.Tags
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

            Input.Lines = recipe.Ingredients
                .OrderBy(i => i.Ordinal)
                .Select((ing, idx) =>
                {
                    productLookup.TryGetValue(ing.ProductId, out var p);
                    var unitCode = ing.UnitId.HasValue ? unitCodeLookup.GetValueOrDefault(ing.UnitId.Value) : null;
                    return new IngredientRowInput
                    {
                        Ordinal = idx,
                        ProductId = ing.ProductId,
                        ProductName = p?.Name ?? "",
                        Quantity = ing.Quantity,
                        UnitId = ing.UnitId,
                        UnitCode = unitCode,
                        GroupHeading = ing.GroupHeading,
                    };
                }).ToList();

            OriginalServings = recipe.DefaultServings;
            HasExistingIngredients = recipe.Ingredients.Count > 0;
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
        // The <li> dispatches a custom Alpine event carrying all product data. The surrounding
        // .ingredient-row catches 'pick-product' and calls selectProduct(row, $event.detail)
        // with access to the current x-for 'row' scope — avoids the nested x-data scope conflict
        // that would occur with a single-arg $el approach.
        var html = string.Join("", hits.Select(p =>
            $$"""<li role="option" data-value="{{p.Id}}" data-track="{{(p.TrackStock ? "true" : "false")}}" data-default-unit="{{p.DefaultUnitId}}" @click="query = $el.textContent.trim(); open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.textContent.trim(), track: $el.dataset.track})">{{enc.Encode(p.Name)}}</li>"""));
        return Content(html, "text/html");
    }

    // ── POST — main save ─────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAsync(IFormFile? photo, CancellationToken ct)
    {
        await LoadReferenceDataAsync(ct);

        if (!ModelState.IsValid)
            return Page();

        // Build the ingredient lines from form input
        var lines = Input.Lines
            .Where(l => l.ProductId.HasValue || !string.IsNullOrWhiteSpace(l.NewStapleName))
            .Select(l => new AuthorIngredientLine(
                ProductId: l.ProductId,
                Quantity: l.Quantity,
                UnitId: l.UnitId,
                GroupHeading: string.IsNullOrWhiteSpace(l.GroupHeading) ? null : l.GroupHeading.Trim(),
                Ordinal: l.Ordinal,
                NewStapleName: l.NewStapleName,
                NewStapleDefaultUnitId: l.NewStapleDefaultUnitId,
                ConversionFactor: l.ConversionFactor))
            .ToList();

        if (lines.Count == 0)
        {
            SaveError = "A recipe must have at least one ingredient.";
            RestoreLines();
            return Page();
        }

        var tagNames = (Input.Tags ?? [])
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var command = new AuthorRecipeCommand(
            RecipeId: Id.HasValue ? RecipeId.From(Id.Value) : null,
            Name: Input.Name?.Trim() ?? "",
            DefaultServings: Input.DefaultServings,
            Lines: lines,
            TagNames: tagNames,
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

    private async Task LoadReferenceDataAsync(CancellationToken ct)
    {
        // Unit list via the anti-corruption port — never through IUnitRepository directly (Gate 2).
        var unitOptions = await products.ListUnitsAsync(ct);
        UnitOptions = unitOptions
            .Select(u => new SelectListItem(u.Code, u.Id.ToString()))
            .ToList();

        // Tag options from household: resolve known tags for the multi-select completion.
        // Tags are a small set (dozens, not thousands) — list all and filter client-side.
        TagOptions = [];
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

    private void RestoreLines()
    {
        // Drop blank rows (no product chosen and no staple name) so the read-only ingredient list
        // doesn't render ghost entries after a failed save. Rows flagged for conversion keep their
        // ProductId, so the NeedsConversion annotations set above survive this filter.
        Input.Lines = Input.Lines
            .Where(l => l.ProductId.HasValue || !string.IsNullOrWhiteSpace(l.NewStapleName))
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

    /// <summary>Tag names (typed strings; resolved or minted by AuthorRecipe).</summary>
    public List<string> Tags { get; set; } = [];

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
/// or a new-staple request (NewStapleName + NewStapleDefaultUnitId, C12). NeedsConversion and the
/// two unit ids are set by the page model on a NeedsConversion outcome so the view can display the
/// inline conversion form (C10).
/// </summary>
public sealed class IngredientRowInput
{
    public int Ordinal { get; set; }

    // ── Product ────────────────────────────────────────────────────────────────────
    public Guid? ProductId { get; set; }
    /// <summary>Display-only — not posted; populated by Alpine when the user picks from the search list.</summary>
    public string? ProductName { get; set; }

    // ── Inline staple create (C12) ─────────────────────────────────────────────────
    public string? NewStapleName { get; set; }
    public Guid? NewStapleDefaultUnitId { get; set; }

    // ── Quantity / unit ────────────────────────────────────────────────────────────
    public decimal? Quantity { get; set; }
    public Guid? UnitId { get; set; }
    /// <summary>Display-only — the unit code string (e.g. "g") for pre-populating the select label on edit.</summary>
    public string? UnitCode { get; set; }

    // ── Optional group heading ─────────────────────────────────────────────────────
    public string? GroupHeading { get; set; }

    // ── Inline conversion form (C10 — set on NeedsConversion outcome) ─────────────
    public bool NeedsConversion { get; set; }
    public Guid ConversionFromUnitId { get; set; }
    public Guid ConversionToUnitId { get; set; }
    public decimal? ConversionFactor { get; set; }
}
