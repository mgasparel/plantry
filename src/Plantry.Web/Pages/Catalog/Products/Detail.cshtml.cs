using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Pages.Catalog.Products;

[Authorize]
public sealed class DetailModel(
    IProductRepository products,
    IUnitRepository units,
    ICategoryRepository categories,
    ILocationRepository locations,
    IProductStockRepository stocks,
    ProductQueryService queries,
    IClock clock,
    ITenantContext tenant,
    ILogger<UpdateProductCommand> updateProductLogger,
    ILogger<AddSkuCommand> addSkuLogger,
    ILogger<AddConversionCommand> addConversionLogger,
    ILogger<PromoteConversionCommand> promoteConversionLogger,
    ILogger<MakeVariantCommand> makeVariantLogger,
    ILogger<CreateVariantCommand> createVariantLogger,
    ApplyDeferredUnitGaps deferredUnitGaps,
    ILogger<DetailModel> logger) : PageModel
{
    public ProductId Id { get; private set; }
    public ProductDetail? Product { get; private set; }

    /// <summary>
    /// True when a <see cref="Plantry.Inventory.Domain.ProductStock"/> record exists for this
    /// product in the current household — i.e. it has been stocked at least once and the Pantry
    /// detail page would render rather than 404. Drives the "View in pantry" cross-link: live when
    /// true, muted "Not in pantry yet" hint when false (plantry-kkeg). Mirrors the exact existence
    /// condition in <c>InventoryQueryService.FindDetailAsync</c> (a record, not active lots — a
    /// product consumed to zero still has a stock record and a live pantry view).
    /// </summary>
    public bool HasPantryStock { get; private set; }

    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> LocationOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ParentOptions { get; private set; } = [];

    public AddSkuInputModel SkuInput { get; set; } = new();
    public AddConversionInputModel ConversionInput { get; set; } = new();
    public MakeVariantInputModel VariantInput { get; set; } = new();
    public AddVariantInputModel AddVariantInput { get; set; } = new();

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
        SeedAddVariantInput(entity!);
        await LoadOptionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, [Bind(Prefix = "Input")] InputModel input)
    {
        Id = ProductId.From(id);
        Input = input;
        if (!ModelState.IsValid) return await ReloadAsync(keepInput: true);

        var cmd = new UpdateProductCommand(
            Id, Input.Name, Input.DefaultUnitId!.Value, Input.CategoryId, Input.DefaultLocationId,
            Input.DefaultDueDays, Input.DefaultDueDaysAfterOpening, Input.DefaultDueDaysAfterFreezing,
            Input.DefaultDueDaysAfterThawing, products, units, categories, locations, clock,
            logger: updateProductLogger);

        var result = await cmd.ExecuteAsync();
        if (result.IsFailure)
        {
            if (result.Error == Plantry.SharedKernel.Error.NotFound) return NotFound();
            ModelState.AddModelError(string.Empty, result.Error.Description);
            return await ReloadAsync(keepInput: true);
        }

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

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveSkuAsync(Guid id, Guid skuId)
    {
        await new RemoveSkuCommand(ProductId.From(id), ProductSkuId.From(skuId), products, clock).ExecuteAsync();
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

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveConversionAsync(Guid id, Guid conversionId)
    {
        await new RemoveConversionCommand(ProductId.From(id), ProductConversionId.From(conversionId), products, clock).ExecuteAsync();
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
        return RedirectToPage(new { id = result.Value.Value });
    }

    public async Task<IActionResult> OnPostDetachAsync(Guid id)
    {
        await new DetachProductFromParentCommand(ProductId.From(id), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id)
    {
        await new ArchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnarchiveAsync(Guid id)
    {
        await new UnarchiveProductCommand(ProductId.From(id), products, clock).ExecuteAsync();
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

        // Always re-seed the add-variant name from the parent/this product so the field is
        // populated on reload (e.g. validation error on another sub-form).
        if (AddVariantInput.Name == string.Empty)
            SeedAddVariantInput(entity!);

        await LoadOptionsAsync();
        return Page();
    }

    /// <summary>
    /// Pre-seeds the "Add a variant" name field with the product's own name so the user only
    /// needs to overtype what differs (the variant's distinguishing suffix/name).
    /// For a parent product we seed from the parent itself so new siblings start from the same
    /// name stem as existing siblings.
    /// </summary>
    private void SeedAddVariantInput(Plantry.Catalog.Domain.Product product)
    {
        AddVariantInput = new AddVariantInputModel
        {
            Name = product.Name,
        };
    }

    private void PopulateInputFromEntity(Plantry.Catalog.Domain.Product product)
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
        };
    }

    private async Task LoadOptionsAsync()
    {
        UnitOptions = (await units.ListAsync())
            .Select(u => new SelectListItem($"{u.Code} — {u.Name}", u.Id.Value.ToString()))
            .ToList();

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
    }
}
