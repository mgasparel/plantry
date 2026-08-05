using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;
using Plantry.Web.TagHelpers;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class DetailModel(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IProductStockRepository stocks,
    ProductQueryService queries,
    IHouseholdExpiryDefaultsReader expiryDefaults,
    IClock clock,
    ITenantContext tenant,
    ILogger<UpdateProductCommand> updateProductLogger,
    ILogger<AddSkuCommand> addSkuLogger,
    ILogger<AddConversionCommand> addConversionLogger,
    ILogger<PromoteConversionCommand> promoteConversionLogger,
    ILogger<MakeVariantCommand> makeVariantLogger,
    ILogger<CreateVariantCommand> createVariantLogger,
    ApplyDeferredUnitGaps deferredUnitGaps,
    // Substitutions (plantry-aqpa.5) — the Recipes-owned concept is authored here because this is
    // where the household is standing when they think "what can stand in for this?" (composition,
    // not a context violation: the page model calls Recipes' own application seams directly, the
    // same pattern Cook.cshtml.cs already uses for the fulfillment/cook-picker touchpoints).
    ISubstitutionReader substitutionReader,
    ISubstitutionRepository substitutionRepository,
    ILogger<CreateSubstitution> createSubstitutionLogger,
    ILogger<DeleteSubstitution> deleteSubstitutionLogger,
    ILogger<DetailModel> logger) : PageModel
{
    public ProductId Id { get; private set; }
    public ProductDetail? Product { get; private set; }

    /// <summary>
    /// True when a <see cref="Plantry.Pantry.Domain.ProductStock"/> record exists for this
    /// product in the current household — i.e. it has been stocked at least once. Drives the
    /// "View in pantry" cross-link: live when true, muted "Not in pantry yet" hint when false
    /// (plantry-kkeg). This is a "has stock history" check, not a "would the Pantry detail page
    /// 404" check — since plantry-sjfn, <c>InventoryQueryService.FindDetailAsync</c> renders a
    /// zero-lot empty state for any catalog product (stocked or not) rather than 404ing, so the
    /// Pantry detail page always renders now. The cross-link intentionally stays conservative
    /// (kept as the "safety net between the two views" the design called for) rather than always
    /// linking through to a page the product has never actually touched.
    /// </summary>
    public bool HasPantryStock { get; private set; }

    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ParentOptions { get; private set; } = [];
    public ExpiryPolicyEditorViewModel FreezingPolicy { get; private set; } = null!;
    public ExpiryPolicyEditorViewModel ThawingPolicy { get; private set; } = null!;

    public AddSkuInputModel SkuInput { get; set; } = new();
    public AddConversionInputModel ConversionInput { get; set; } = new();
    public MakeVariantInputModel VariantInput { get; set; } = new();
    public AddVariantInputModel AddVariantInput { get; set; } = new();

    /// <summary>Every substitution edge touching this product, both directions (plantry-aqpa.5).</summary>
    public IReadOnlyList<SubstitutionListItem> Substitutions { get; private set; } = [];
    public AddSubstitutionInputModel SubstitutionInput { get; set; } = new();

    /// <summary>Every other active product this household could pick as the substitute/target side.</summary>
    public IReadOnlyList<SelectListItem> SubstitutionProductOptions { get; private set; } = [];

    public sealed class InputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Name")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a default unit.")]
        [Display(Name = "Default unit")]
        public Guid? DefaultUnitId { get; set; }

        [Display(Name = "Category")]
        public Guid? CategoryId { get; set; }

        [Display(Name = "Default location")]
        public Guid? DefaultLocationId { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Default expiry (days)")]
        public int? DefaultDueDays { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Expiry after opening (days)")]
        public int? DefaultDueDaysAfterOpening { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Expiry after freezing (days)")]
        public int? DefaultDueDaysAfterFreezing { get; set; }

        [Range(0, 3650)]
        [Display(Name = "Expiry after thawing (days)")]
        public int? DefaultDueDaysAfterThawing { get; set; }

        /// <summary>The selected local policy mode. Nullable keeps older form posts backward-compatible.</summary>
        public ProductExpiryMode? AfterFreezingMode { get; set; }

        /// <summary>The selected local policy mode. Nullable keeps older form posts backward-compatible.</summary>
        public ProductExpiryMode? AfterThawingMode { get; set; }

        /// <summary>
        /// Whether this product participates in quantity accounting (Product.TrackStock). Hidden
        /// on the form for a parent product (grouping abstraction, can't hold stock) — for those,
        /// <see cref="UpdateProductCommand"/> ignores whatever value posts here and leaves the
        /// flag untouched, so a stale/default value from an unrendered field can't flip it.
        /// </summary>
        [Display(Name = "Track stock")]
        public bool TrackStock { get; set; } = true;
    }

    public sealed class AddSkuInputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Label")]
        public string Label { get; set; } = string.Empty;

        [Range(0.000001, double.MaxValue, ErrorMessage = "Size quantity must be positive when provided.")]
        [Display(Name = "Size quantity")]
        public decimal? SizeQuantity { get; set; }

        [Display(Name = "Size unit")]
        public Guid? SizeUnitId { get; set; }
    }

    public sealed class AddConversionInputModel
    {
        [Required(ErrorMessage = "Select a from-unit.")]
        [Display(Name = "From unit")]
        public Guid? FromUnitId { get; set; }

        [Required(ErrorMessage = "Select a to-unit.")]
        [Display(Name = "To unit")]
        public Guid? ToUnitId { get; set; }

        [Range(0.000001, double.MaxValue, ErrorMessage = "Factor must be positive.")]
        [Display(Name = "Factor")]
        public decimal Factor { get; set; }
    }

    /// <summary>
    /// Input for authoring a <see cref="Plantry.Recipes.Domain.Substitution"/> edge from this product's
    /// detail page (plantry-aqpa.5). <see cref="Direction"/> picks which side of the edge this product
    /// is on: "in" — the other product satisfies THIS one (this product is the edge's target); "out" —
    /// THIS product satisfies the other one (this product is the edge's substitute). No edit in v1 —
    /// delete + re-add is the repair path, matching <c>ProductConversion</c>.
    /// </summary>
    public sealed class AddSubstitutionInputModel
    {
        // RegularExpression (not just Required) — Direction picks which side of the edge THIS product
        // lands on; a stale/garbled posted value that is neither "in" nor "out" must round-trip as a
        // field error rather than the OnPostAddSubstitutionAsync isIncoming check silently treating any
        // non-"in" value as "out" and authoring the edge in the reverse direction.
        [Required(ErrorMessage = "Select a direction.")]
        [RegularExpression("^(in|out)$", ErrorMessage = "Select a direction.")]
        public string Direction { get; set; } = "in";

        [Required(ErrorMessage = "Select the other product.")]
        [Display(Name = "Other product")]
        public Guid? OtherProductId { get; set; }

        [Required(ErrorMessage = "Enter the amount of this product.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be positive.")]
        [Display(Name = "Amount of this product")]
        public decimal? ThisQuantity { get; set; }

        [Required(ErrorMessage = "Select a unit for this product.")]
        [Display(Name = "Unit for this product")]
        public Guid? ThisUnitId { get; set; }

        [Required(ErrorMessage = "Enter the amount of the other product.")]
        [Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be positive.")]
        [Display(Name = "Amount of the other product")]
        public decimal? OtherQuantity { get; set; }

        [Required(ErrorMessage = "Select a unit for the other product.")]
        [Display(Name = "Unit for the other product")]
        public Guid? OtherUnitId { get; set; }
    }

    public sealed class MakeVariantInputModel
    {
        [Required(ErrorMessage = "Select a parent product.")]
        [Display(Name = "Parent product")]
        public Guid? ParentProductId { get; set; }
    }

    /// <summary>Input for the inline "Add a variant" form on a parent or standalone product's detail page.</summary>
    public sealed class AddVariantInputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Variant name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>When null, inherits from parent.</summary>
        [Display(Name = "Default unit override")]
        public Guid? DefaultUnitId { get; set; }

        /// <summary>When null, inherits from parent.</summary>
        [Display(Name = "Category override")]
        public Guid? CategoryId { get; set; }

        /// <summary>When null, inherits from parent.</summary>
        [Display(Name = "Default location override")]
        public Guid? DefaultLocationId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Id = ProductId.From(id);
        Product = await queries.FindDetailAsync(Id);
        if (Product is null) return NotFound();

        await LoadPantryStockStateAsync();
        var entity = await products.FindAsync(Id);
        PopulateInputFromEntity(entity!);
        await LoadExpiryPolicyEditorsAsync(entity!);
        SeedAddVariantInput(entity!);
        await LoadOptionsAsync();
        await LoadSubstitutionsAsync();
        return Page();
    }

    /// <summary>
    /// htmx typeahead endpoint backing the "Add substitution" other-product search (plantry-aqpa.5) —
    /// the existing product search/typeahead pattern (mirrors Pantry Index's
    /// <c>OnGetFilterProductsAsync</c>). Excludes this product itself (self-substitution is a domain
    /// error the picker should never even offer).
    /// </summary>
    public async Task<ContentResult> OnGetFilterSubstitutionProductsAsync(Guid id, string? q)
    {
        var matches = (await products.ListActiveAsync(HttpContext.RequestAborted))
            .Where(p => p.Id.Value != id)
            .Where(p => string.IsNullOrWhiteSpace(q) || p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(p => new SelectListItem(p.Name, p.Id.Value.ToString()));

        var html = new StringBuilder();
        SearchableSelectTagHelper.AppendOptions(html, matches, HtmlEncoder.Default);
        return Content(html.ToString(), "text/html");
    }

    public async Task<IActionResult> OnPostAsync(Guid id, [Bind(Prefix = "Input")] InputModel input)
    {
        Id = ProductId.From(id);
        Input = input;
        var entity = await products.FindAsync(Id, HttpContext.RequestAborted);
        if (entity is null) return NotFound();

        var afterFreezing = ResolvePostedPolicy(
            entity, Input.AfterFreezingMode, Input.DefaultDueDaysAfterFreezing, thawing: false);
        var afterThawing = ResolvePostedPolicy(
            entity, Input.AfterThawingMode, Input.DefaultDueDaysAfterThawing, thawing: true);
        if (!ModelState.IsValid) return await ReloadAsync(keepInput: true);

        var cmd = new UpdateProductCommand(
            Id, Input.Name, Input.DefaultUnitId!.Value, Input.CategoryId, Input.DefaultLocationId,
            Input.DefaultDueDays, Input.DefaultDueDaysAfterOpening, afterFreezing.Days,
            afterThawing.Days, Input.TrackStock, products, units, categories, locations, clock,
            logger: updateProductLogger,
            neverExpiresAfterFreezing: afterFreezing.Never,
            neverExpiresAfterThawing: afterThawing.Never);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            if (result.Error == Plantry.SharedKernel.Error.NotFound) return NotFound();
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadAsync(keepInput: true);
        }

        TempData["ToastMessage"] = "Product updated.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddSkuAsync(Guid id, [Bind(Prefix = "SkuInput")] AddSkuInputModel input)
    {
        Id = ProductId.From(id);
        SkuInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new AddSkuCommand(Id, SkuInput.Label, SkuInput.SizeQuantity, SkuInput.SizeUnitId, products, units, clock, addSkuLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        TempData["ToastMessage"] = "SKU added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveSkuAsync(Guid id, Guid skuId)
    {
        await new RemoveSkuCommand(ProductId.From(id), ProductSkuId.From(skuId), products, clock).ExecuteAsync();
        TempData["ToastMessage"] = "SKU removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddConversionAsync(Guid id, [Bind(Prefix = "ConversionInput")] AddConversionInputModel input)
    {
        Id = ProductId.From(id);
        ConversionInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new AddConversionCommand(
            Id, ConversionInput.FromUnitId!.Value, ConversionInput.ToUnitId!.Value, ConversionInput.Factor,
            products, units, clock, addConversionLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        // A landed conversion may bridge a unit gap a prior cook deferred — retro-apply now (plantry-qll2.6).
        // Not gated by the AI toggle: a manually-entered conversion settles deferred consumes just the same.
        await TryApplyDeferredUnitGapsAsync(id);

        TempData["ToastMessage"] = "Conversion added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveConversionAsync(Guid id, Guid conversionId)
    {
        await new RemoveConversionCommand(ProductId.From(id), ProductConversionId.From(conversionId), products, clock).ExecuteAsync();
        TempData["ToastMessage"] = "Conversion removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPromoteConversionAsync(Guid id, Guid conversionId)
    {
        Id = ProductId.From(id);
        var cmd = new PromoteConversionCommand(Id, ProductConversionId.From(conversionId), products, clock, promoteConversionLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            if (result.Error == Plantry.SharedKernel.Error.NotFound) return NotFound();
            return await ReloadWithErrorAsync(result.Error);
        }

        // Promoting an ai_suggested factor to user_confirmed keeps the pair bridged — settle any deferred
        // consume lines waiting on it now (plantry-qll2.6), matching the manual-add path above.
        await TryApplyDeferredUnitGapsAsync(id);

        TempData["ToastMessage"] = "Conversion confirmed.";
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Authors a <see cref="Plantry.Recipes.Domain.Substitution"/> edge touching this product
    /// (plantry-aqpa.5) — either direction, per <see cref="AddSubstitutionInputModel.Direction"/>.
    /// A duplicate directed pair replaces the existing edge's ratio (the domain's upsert rule,
    /// <see cref="CreateSubstitution"/>) rather than being rejected — surfaced here as a distinct toast
    /// message so the household can tell "added" from "replaced" apart, per the ticket's "surface the
    /// domain rule as a clear inline message" requirement.
    /// </summary>
    public async Task<IActionResult> OnPostAddSubstitutionAsync(Guid id, [Bind(Prefix = "SubstitutionInput")] AddSubstitutionInputModel input)
    {
        Id = ProductId.From(id);
        SubstitutionInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var isIncoming = input.Direction == "in";
        var otherProductId = input.OtherProductId!.Value;
        var targetProductId = isIncoming ? id : otherProductId;
        var targetQuantity = (isIncoming ? input.ThisQuantity : input.OtherQuantity)!.Value;
        var targetUnitId = (isIncoming ? input.ThisUnitId : input.OtherUnitId)!.Value;
        var substituteProductId = isIncoming ? otherProductId : id;
        var substituteQuantity = (isIncoming ? input.OtherQuantity : input.ThisQuantity)!.Value;
        var substituteUnitId = (isIncoming ? input.OtherUnitId : input.ThisUnitId)!.Value;

        var cmd = new CreateSubstitution(substitutionRepository, tenant, clock, createSubstitutionLogger);
        var result = await cmd.ExecuteAsync(
            new CreateSubstitutionCommand(
                targetProductId, targetQuantity, targetUnitId,
                substituteProductId, substituteQuantity, substituteUnitId),
            HttpContext.RequestAborted);
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        // The command itself already knows whether this upserted onto an existing edge (its own
        // FindByPairAsync lookup) — read that off Result.Value rather than re-deriving it here with a
        // second, redundant read through the write-side ISubstitutionRepository seam.
        TempData["ToastMessage"] = result.Value
            ? "Substitution updated — replaced the existing ratio for this pair."
            : "Substitution added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveSubstitutionAsync(Guid id, Guid substitutionId)
    {
        var cmd = new DeleteSubstitution(substitutionRepository, deleteSubstitutionLogger);
        await cmd.ExecuteAsync(new DeleteSubstitutionCommand(SubstitutionId.From(substitutionId)), HttpContext.RequestAborted);
        TempData["ToastMessage"] = "Substitution removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMakeVariantAsync(Guid id, [Bind(Prefix = "VariantInput")] MakeVariantInputModel input)
    {
        Id = ProductId.From(id);
        VariantInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        var cmd = new MakeVariantCommand(Id, ProductId.From(VariantInput.ParentProductId!.Value), products, clock, makeVariantLogger);
        var result = await cmd.ExecuteAsync();
        if (result.IsFailure) return await ReloadWithErrorAsync(result.Error);

        TempData["ToastMessage"] = "Product made a variant.";
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Handles the "Add a variant" form on a parent or standalone product's detail page.
    /// For a standalone product, checks that no active stock is held before allowing it to become
    /// a parent — stock migration is out of scope (inventory would be stranded under the parent).
    /// </summary>
    public async Task<IActionResult> OnPostAddVariantAsync(Guid id, [Bind(Prefix = "AddVariantInput")] AddVariantInputModel input)
    {
        Id = ProductId.From(id);
        AddVariantInput = input;
        if (!ModelState.IsValid) return await ReloadAsync();

        // Gate: a standalone product that already holds stock cannot become a parent without
        // migrating stock to a variant — out of scope. Check BEFORE calling the command.
        var currentProduct = await products.FindAsync(Id, HttpContext.RequestAborted);
        if (currentProduct is null) return NotFound();

        if (!currentProduct.IsParent && !currentProduct.IsVariant)
        {
            // Standalone: check for active inventory lots.
            if (tenant.HouseholdId is { } householdId)
            {
                var stock = await stocks.FindAsync(HouseholdId.From(householdId), Id.Value, HttpContext.RequestAborted);
                if (stock is not null && stock.ActiveLotsFefo().Any())
                {
                    ModelState.AddModelError(string.Empty,
                        "This product currently holds stock. Remove all stock lots before adding a variant (stock migration is not yet supported).");
                    return await ReloadAsync();
                }
            }
        }

        var cmd = new CreateVariantCommand(
            Id,
            AddVariantInput.Name,
            AddVariantInput.DefaultUnitId,
            AddVariantInput.CategoryId,
            AddVariantInput.DefaultLocationId,
            products, units, categories, locations, clock, tenant, createVariantLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            if (result.Error == Plantry.SharedKernel.Error.NotFound) return NotFound();
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadAsync();
        }

        // Redirect to the newly created variant so the user can see and further edit it.
        TempData["ToastMessage"] = "Variant created.";
        return RedirectToPage(new { id = result.Value.Value });
    }

    public async Task<IActionResult> OnPostDetachAsync(Guid id)
    {
        await new DetachProductFromParentCommand(ProductId.From(id), products, clock).ExecuteAsync();
        TempData["ToastMessage"] = "Product detached from parent.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id)
    {
        await new ArchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
        TempData["ToastMessage"] = "Product archived.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnarchiveAsync(Guid id)
    {
        await new UnarchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
        TempData["ToastMessage"] = "Product unarchived.";
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> ReloadWithErrorAsync(Error error)
    {
        ModelState.AddModelError(string.Empty, error.Description);
        return await ReloadAsync();
    }

    /// <summary>
    /// Sets <see cref="HasPantryStock"/> by checking whether a stock record exists for this product
    /// in the current household (plantry-kkeg). This is the same existence gate the Pantry detail
    /// page uses, so the "View in pantry" cross-link is live exactly when that page would render.
    /// </summary>
    private async Task LoadPantryStockStateAsync()
    {
        HasPantryStock = tenant.HouseholdId is { } householdId
            && await stocks.FindAsync(HouseholdId.From(householdId), Id.Value, HttpContext.RequestAborted) is not null;
    }

    /// <summary>
    /// Best-effort retro-apply of deferred unit-gap consume lines after a conversion lands (plantry-qll2.6).
    /// A convergence follow-up, not part of the conversion write: the conversion is already durably saved,
    /// so a failure here must never fail the request — the opportunistic self-heal at cook entry recovers
    /// any missed application from durable state (ADR-014). Mirrors <c>CookRecipe</c>'s best-effort
    /// reconciliation sweep. <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    private async Task TryApplyDeferredUnitGapsAsync(Guid id)
    {
        try
        {
            await deferredUnitGaps.ExecuteAsync([id], HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Deferred unit-gap retro-apply failed after a conversion landed for product {ProductId}; the next cook of the product self-heals.",
                id);
        }
    }

    private async Task<IActionResult> ReloadAsync(bool keepInput = false)
    {
        Product = await queries.FindDetailAsync(Id);
        if (Product is null) return NotFound();

        await LoadPantryStockStateAsync();
        var entity = await products.FindAsync(Id);
        if (!keepInput)
            PopulateInputFromEntity(entity!);
        await LoadExpiryPolicyEditorsAsync(entity!);

        // Always re-seed the add-variant name from the parent/this product so the field is
        // populated on reload (e.g. validation error on another sub-form).
        if (AddVariantInput.Name == string.Empty)
            SeedAddVariantInput(entity!);

        await LoadOptionsAsync();
        await LoadSubstitutionsAsync();
        return Page();
    }

    /// <summary>
    /// Pre-seeds the "Add a variant" name field with the product's own name so the user only
    /// needs to overtype what differs (the variant's distinguishing suffix/name).
    /// For a parent product we seed from the parent itself so new siblings start from the same
    /// name stem as existing siblings.
    /// </summary>
    private void SeedAddVariantInput(Plantry.Pantry.Domain.Product product)
    {
        AddVariantInput = new AddVariantInputModel
        {
            Name = product.Name,
        };
    }

    private void PopulateInputFromEntity(Plantry.Pantry.Domain.Product product)
    {
        Input = new InputModel
        {
            Name = product.Name,
            DefaultUnitId = product.DefaultUnitId.Value,
            CategoryId = product.CategoryId?.Value,
            DefaultLocationId = product.DefaultLocationId?.Value,
            DefaultDueDays = product.DefaultDueDays,
            DefaultDueDaysAfterOpening = product.DefaultDueDaysAfterOpening,
            DefaultDueDaysAfterFreezing = product.DefaultDueDaysAfterFreezing,
            DefaultDueDaysAfterThawing = product.DefaultDueDaysAfterThawing,
            AfterFreezingMode = InitialMode(product, thawing: false),
            AfterThawingMode = InitialMode(product, thawing: true),
            TrackStock = product.TrackStock,
        };
    }

    private async Task LoadExpiryPolicyEditorsAsync(Plantry.Pantry.Domain.Product product)
    {
        var parent = product.ParentProductId is { } parentId
            ? await products.FindAsync(parentId, HttpContext.RequestAborted)
            : null;
        var household = await expiryDefaults.GetDefaultsAsync(HttpContext.RequestAborted);

        var inheritedFreezing = ExpiryDefaultResolver.ResolveAfterFreezing(product, parent, household.AfterFreezing);
        var inheritedThawing = ExpiryDefaultResolver.ResolveAfterThawing(product, parent, household.AfterThawing);

        FreezingPolicy = BuildExpiryPolicyEditor(
            product, inheritedFreezing, household.AfterFreezing, Input.AfterFreezingMode,
            Input.DefaultDueDaysAfterFreezing, thawing: false);
        ThawingPolicy = BuildExpiryPolicyEditor(
            product, inheritedThawing, household.AfterThawing, Input.AfterThawingMode,
            Input.DefaultDueDaysAfterThawing, thawing: true);
    }

    private ExpiryPolicyEditorViewModel BuildExpiryPolicyEditor(
        Plantry.Pantry.Domain.Product product,
        ExpiryTransitionPolicy inheritedPolicy,
        int householdDays,
        ProductExpiryMode? selectedMode,
        int? localDays,
        bool thawing)
    {
        var mode = selectedMode is { } postedMode
            && Enum.IsDefined(typeof(ProductExpiryMode), postedMode)
            ? postedMode
            : InitialMode(product, thawing);
        // A rejected form post is re-rendered with the bound value still in the page model. Do
        // not feed an out-of-range value back into the value object: its constructor deliberately
        // throws for negative days, while the page needs to survive long enough to show the
        // field-level validation message. A null display value leaves the Set days input blank;
        // the household default is only the safe effective-policy fallback for the preview.
        var safeLocalDays = localDays is >= 0 and <= 3650 ? localDays : null;
        var effectivePolicy = mode switch
        {
            ProductExpiryMode.Never => new ExpiryTransitionPolicy.Never(),
            ProductExpiryMode.SetDays => new ExpiryTransitionPolicy.Days(safeLocalDays ?? householdDays),
            ProductExpiryMode.Inherit => inheritedPolicy,
            _ => new ExpiryTransitionPolicy.Days(householdDays),
        };

        return new ExpiryPolicyEditorViewModel(
            FieldPrefix: thawing ? "DefaultDueDaysAfterThawing" : "DefaultDueDaysAfterFreezing",
            Label: thawing ? "Expiry after thawing" : "Expiry after freezing",
            IsVariant: product.IsVariant,
            Mode: mode,
            HouseholdDays: householdDays,
            LocalDays: safeLocalDays,
            InheritedNever: inheritedPolicy is ExpiryTransitionPolicy.Never,
            InheritedDays: inheritedPolicy is ExpiryTransitionPolicy.Days inheritedDays ? inheritedDays.Value : null,
            EffectiveNever: effectivePolicy is ExpiryTransitionPolicy.Never,
            EffectiveDays: effectivePolicy is ExpiryTransitionPolicy.Days effectiveDays ? effectiveDays.Value : null);
    }

    private (bool? Never, int? Days) ResolvePostedPolicy(
        Plantry.Pantry.Domain.Product product,
        ProductExpiryMode? mode,
        int? days,
        bool thawing)
    {
        var modeFieldName = $"Input.{(thawing ? "AfterThawingMode" : "AfterFreezingMode")}";
        if (IsUndefinedPostedMode(mode, modeFieldName))
        {
            ModelState[modeFieldName]?.Errors.Clear();
            ModelState.AddModelError(
                modeFieldName,
                "Select a valid expiry policy.");
            return (thawing ? product.NeverExpiresAfterThawing : product.NeverExpiresAfterFreezing, days);
        }

        if (mode is null)
        {
            return thawing
                ? (product.NeverExpiresAfterThawing, days)
                : (product.NeverExpiresAfterFreezing, days);
        }

        if (mode == ProductExpiryMode.SetDays && days is null)
        {
            ModelState.AddModelError(
                $"Input.{(thawing ? "DefaultDueDaysAfterThawing" : "DefaultDueDaysAfterFreezing")}",
                "Enter the number of days for a custom expiry policy.");
            return (false, null);
        }

        if (!product.IsVariant && mode == ProductExpiryMode.Inherit)
        {
            ModelState.AddModelError(string.Empty, "A root product cannot inherit an expiry policy.");
            return (null, days);
        }

        return mode switch
        {
            ProductExpiryMode.Never => (true, null),
            ProductExpiryMode.SetDays => (false, days),
            ProductExpiryMode.Default => (product.IsVariant ? false : null, null),
            // Day-count inheritance remains the Catalog snapshot convention; the new Never decision
            // is the live nullable contract. Keeping the stored day value here avoids silently
            // replacing a variant's existing snapshot when only its Never mode is changed.
            ProductExpiryMode.Inherit => (null, thawing
                ? product.DefaultDueDaysAfterThawing
                : product.DefaultDueDaysAfterFreezing),
            _ => (null, days),
        };
    }

    private bool IsUndefinedPostedMode(ProductExpiryMode? mode, string modeFieldName)
    {
        if (mode is { } postedMode)
            return !Enum.IsDefined(typeof(ProductExpiryMode), postedMode);

        var attemptedValue = ModelState[modeFieldName]?.AttemptedValue;
        return int.TryParse(attemptedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue)
            && !Enum.IsDefined(typeof(ProductExpiryMode), numericValue);
    }

    private static ProductExpiryMode InitialMode(Plantry.Pantry.Domain.Product product, bool thawing)
    {
        var never = thawing ? product.NeverExpiresAfterThawing : product.NeverExpiresAfterFreezing;
        var days = thawing ? product.DefaultDueDaysAfterThawing : product.DefaultDueDaysAfterFreezing;

        if (never == true) return ProductExpiryMode.Never;
        if (product.IsVariant && never is null) return ProductExpiryMode.Inherit;
        if (never == false && days is not null) return ProductExpiryMode.SetDays;
        if (days is not null) return ProductExpiryMode.SetDays;
        return ProductExpiryMode.Default;
    }

    private async Task LoadOptionsAsync()
    {
        UnitOptions = UnitSelectListBuilder.BuildFromUnits(
            await units.ListAsync(),
            u => u.Id.Value.ToString(),
            u => $"{u.Code} — {u.Name}");

        var categoryOptions = (await categories.ListActiveAsync())
            .Select(c => new SelectListItem(c.Name, c.Id.Value.ToString()))
            .ToList();
        if (Input.CategoryId is { } categoryId && categoryOptions.All(o => o.Value != categoryId.ToString())
            && await categories.FindAsync(CategoryId.From(categoryId)) is { } category)
        {
            categoryOptions.Insert(0, new SelectListItem($"{category.Name} (archived)", category.Id.Value.ToString()));
        }
        CategoryOptions = categoryOptions;

        var locationOptions = (await locations.ListActiveAsync())
            .Select(l => new SelectListItem(l.Name, l.Id.Value.ToString()))
            .ToList();
        if (Input.DefaultLocationId is { } locationId && locationOptions.All(o => o.Value != locationId.ToString())
            && await locations.FindAsync(LocationId.From(locationId)) is { } location)
        {
            locationOptions.Insert(0, new SelectListItem($"{location.Name} (archived)", location.Id.Value.ToString()));
        }
        LocationOptions = locationOptions;

        var candidates = await products.ListActiveAsync();
        ParentOptions = candidates
            .Where(p => p.Id != Id && !p.IsVariant)
            .Select(p => new SelectListItem(p.Name, p.Id.Value.ToString()))
            .ToList();

        // Substitutions (plantry-aqpa.5) may target/be-satisfied-by any other active product — no
        // parent/variant restriction the way ParentOptions has, since a substitution edge is a plain
        // catalog-product-to-catalog-product relationship.
        SubstitutionProductOptions = candidates
            .Where(p => p.Id != Id)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SelectListItem(p.Name, p.Id.Value.ToString()))
            .ToList();
    }

    /// <summary>
    /// Loads every substitution edge touching this product, both directions, phrased from this
    /// product's point of view (plantry-aqpa.5) — mirrors <see cref="LoadOptionsAsync"/>'s "reload
    /// alongside the rest of the page" role, called from both <see cref="OnGetAsync"/> and
    /// <see cref="ReloadAsync"/>.
    /// </summary>
    private async Task LoadSubstitutionsAsync()
    {
        var edges = await substitutionReader.ListTouchingProductAsync(Id.Value, HttpContext.RequestAborted);
        if (edges.Count == 0)
        {
            Substitutions = [];
            return;
        }

        var otherProductIds = edges
            .Select(e => e.TargetProductId == Id.Value ? e.SubstituteProductId : e.TargetProductId)
            .Distinct()
            .Select(ProductId.From)
            .ToList();
        var otherProducts = await products.ListByIdsAsync(otherProductIds, HttpContext.RequestAborted);
        var otherProductNames = otherProducts.ToDictionary(p => p.Id.Value, p => p.Name);

        var unitCodesById = (await units.ListAsync(HttpContext.RequestAborted)).ToDictionary(u => u.Id.Value, u => u.Code);
        string UnitCode(Guid unitId) => unitCodesById.GetValueOrDefault(unitId, "?");
        string ProductName(Guid productId) => otherProductNames.GetValueOrDefault(productId, "(unknown product)");

        Substitutions = edges
            .Select(edge =>
            {
                var isIncoming = edge.TargetProductId == Id.Value;
                var otherProductId = isIncoming ? edge.SubstituteProductId : edge.TargetProductId;
                var otherName = ProductName(otherProductId);
                var primaryText = isIncoming ? $"Satisfied by {otherName}" : $"Stands in for {otherName}";
                var ratioText = isIncoming
                    ? $"{edge.SubstituteQuantity.ToString("0.######")} {UnitCode(edge.SubstituteUnitId)} of {otherName} ≡ {edge.TargetQuantity.ToString("0.######")} {UnitCode(edge.TargetUnitId)} of this"
                    : $"{edge.SubstituteQuantity.ToString("0.######")} {UnitCode(edge.SubstituteUnitId)} of this ≡ {edge.TargetQuantity.ToString("0.######")} {UnitCode(edge.TargetUnitId)} of {otherName}";
                return new SubstitutionListItem(edge.Id, isIncoming, otherProductId, otherName, primaryText, ratioText);
            })
            // Incoming ("satisfied by") edges first, then outgoing ("stands in for") — a stable,
            // direction-grouped order rather than raw storage order.
            .OrderByDescending(s => s.IsIncoming)
            .ThenBy(s => s.OtherProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// One row of the product detail "Substitutions" list (plantry-aqpa.5) — a display-ready projection of
/// a <see cref="Plantry.Recipes.Application.SubstitutionEdge"/> phrased from the current product's point
/// of view. <see cref="IsIncoming"/> distinguishes "another product satisfies this one" (edge's target
/// is this product) from "this product satisfies another" (edge's substitute is this product).
/// </summary>
public sealed record SubstitutionListItem(
    Guid Id,
    bool IsIncoming,
    Guid OtherProductId,
    string OtherProductName,
    string PrimaryText,
    string RatioText);

public enum ProductExpiryMode
{
    Default,
    SetDays,
    Never,
    Inherit,
}

public sealed record ExpiryPolicyEditorViewModel(
    string FieldPrefix,
    string Label,
    bool IsVariant,
    ProductExpiryMode Mode,
    int HouseholdDays,
    int? LocalDays,
    bool InheritedNever,
    int? InheritedDays,
    bool EffectiveNever,
    int? EffectiveDays);
