using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Catalog.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Pages.Shared;

namespace Plantry.Web.Pages.Pantry.TakeStock;

/// <summary>
/// Walk page for one Location (P4-4b, J2/J4). Shows the C5 union product rows with count inputs
/// (stepper + display-unit, reason selector), wired to <see cref="SaveCountsCommand"/> on POST.
///
/// Also hosts the lot escape-hatch panel (P4-5, J3): GET /Lots returns a per-(product, location)
/// lot list fragment; POST /SaveLots applies per-lot <see cref="SaveLotAdjustmentsCommand"/>
/// adjustments and returns a per-item result JSON.
///
/// Inline-add (P4-7, J5): GET /SearchProducts returns <c>&lt;li&gt;</c> markup for the shared
/// product-search sheet; POST /AddItem runs <see cref="AddCountedItemCommand"/> (create + opening
/// balance) and returns the new product row as JSON for Alpine to inject into the working set.
///
/// Alpine owns the working set until the user taps Save. Save POSTs a JSON body of dirty rows only;
/// the handler runs <see cref="SaveCountsCommand"/> and returns a per-item result JSON for the
/// client to reflect success/failure on each row without a page reload (C7, TS-6).
/// </summary>
[Authorize]
public sealed class WalkModel(
    ITakeStockReader reader,
    ITakeStockCatalogWriter catalogWriter,
    IUnitRepository unitRepository,
    IProductRepository productRepository,
    ICategoryRepository categoryRepository,
    IProductStockRepository stocks,
    IProductConversionProvider conversions,
    IClock clock,
    ITenantContext tenant,
    ILogger<WalkModel> logger) : PageModel
{
    // ── Read model ────────────────────────────────────────────────────────────

    [BindProperty(SupportsGet = true)]
    public Guid LocationId { get; set; }

    public string? LocationName { get; private set; }

    public IReadOnlyList<TakeStockLocationProductRow> Rows { get; private set; } = [];

    /// <summary>
    /// JSON hydration array for the Preact island — the island renders the whole row (product name,
    /// recorded quantity display, supported units, lots URL) plus the mutable draft state.
    /// </summary>
    public string IslandRowsJson { get; private set; } = "[]";

    /// <summary>Unit options for the inline-add sheet's unit selector.</summary>
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    /// <summary>
    /// Existing group (parent) products for the group combobox in the create view (plantry-40n6).
    /// Passed to <see cref="ProductSearchCreateSheetViewModel.GroupOptions"/> so the Alpine combobox
    /// can filter client-side without an extra htmx round-trip.
    /// </summary>
    public IReadOnlyList<GroupOption> GroupOptions { get; private set; } = [];

    /// <summary>Category options for the Defaults collapsible in the create view (plantry-y53t).</summary>
    public IReadOnlyList<SelectListItem> CategoryOptions { get; private set; } = [];

    // ── GET ───────────────────────────────────────────────────────────────────

    public async Task OnGetAsync(CancellationToken ct = default) =>
        await LoadAsync(ct);

    // ── GET (Product search — inline add sheet, P4-7) ─────────────────────────

    /// <summary>
    /// Returns &lt;li role="option"&gt; markup for the inline-add product search.
    /// Called by htmx on keyup in the shared product-search sheet's input.
    /// Only tracked, non-archived products are returned (same filter as <see cref="ITakeStockReader.SearchProductsAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnGetSearchProductsAsync(string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Content("", "text/html");

        var hits = await reader.SearchProductsAsync(q.Trim(), ct);
        var enc = HtmlEncoder.Default;

        // Emit ranked <li> markup. The ranking label (.rk span) mirrors Intake's AlternativesStrip
        // vocabulary (best / N%) for cross-feature consistency per the design (plantry-hl4a §3).
        // The product name is stored in data-name so the click handler uses data-name rather than
        // textContent (which would otherwise include the .rk label text).
        var html = string.Join("", hits.Select((p, i) =>
        {
            var label = ProductNameMatcher.RankLabel(p.Score, isTopHit: i == 0);
            return $$"""<li role="option" data-value="{{p.ProductId}}" data-name="{{enc.Encode(p.Name)}}" data-track="true" data-default-location="{{p.DefaultLocationId}}" data-default-unit="{{p.DefaultUnitId}}" @click="query = $el.dataset.name; open = false; $dispatch('pick-product', {value: $el.dataset.value, name: $el.dataset.name, track: 'true', defaultUnitId: $el.dataset.defaultUnit})">{{enc.Encode(p.Name)}}<span class="rk">{{enc.Encode(label)}}</span></li>""";
        }));
        return Content(html, "text/html");
    }

    // ── POST (Add item — inline create + opening balance, P4-7) ─────────────────

    /// <summary>
    /// Accepts a JSON body
    /// <c>{ name, defaultUnitId, countedValue, countedUnitId }</c>
    /// posted by the Alpine inline-add sheet's saveSheet() function.
    ///
    /// Runs <see cref="AddCountedItemCommand"/> (create tracked product → RecordCount opening
    /// balance) and returns a JSON response the Alpine client uses to inject the new product row
    /// into the working set:
    /// <c>{ isSuccess, productId, productName, unitCode, unitId, countedValue, error? }</c>.
    ///
    /// The new row is added to Alpine's <c>rows</c> map at the current recorded quantity (the
    /// just-applied opening balance) so the row shows as saved (not dirty) immediately.
    /// </summary>
    public async Task<IActionResult> OnPostAddItemAsync(CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        AddItemRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AddItemRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
            return BadRequest(new { error = "Name is required." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;

        // ── Route to the correct creation path based on group-aware fields ────
        // Path A: join existing group (newGroupId non-empty) → CreateVariantCommand via ITakeStockCatalogWriter
        // Path B: create new group + variant (newGroupName non-empty, newGroupId empty) → CreateGroupedProductCommand
        // Path C: standalone product (no group) → CreateProductCommand (existing AddCountedItemCommand)
        //
        // The count + opening-balance recording is common to all three paths:
        // AddCountedItemCommand handles Path C internally; Paths A/B delegate to catalogWriter
        // then run RecordCountCommand directly (same pattern, same post-create steps).

        var name        = payload.Name.Trim();
        var unitId      = payload.DefaultUnitId;
        var countUnit   = payload.CountedUnitId == Guid.Empty ? unitId : payload.CountedUnitId;
        var newGroupId  = payload.NewGroupId?.Trim() ?? string.Empty;
        var newGroupName = payload.NewGroupName?.Trim() ?? string.Empty;

        Result<Guid> result;

        if (!string.IsNullOrEmpty(newGroupId) && Guid.TryParse(newGroupId, out var parentGroupId))
        {
            // Path A: create as variant of an existing group.
            result = await CreateAndCountAsync(
                createAsync: ct2 => catalogWriter.CreateTrackedVariantAsync(
                    parentGroupId, name,
                    unitOverride:     unitId == Guid.Empty ? null : unitId,
                    categoryOverride: payload.CategoryId,
                    locationOverride: LocationId,
                    ct2),
                countedValue: payload.CountedValue,
                countUnit:    countUnit,
                userId:       userId,
                ct:           ct);
        }
        else if (!string.IsNullOrEmpty(newGroupName))
        {
            // Path B: create new group + first variant atomically.
            result = await CreateAndCountAsync(
                createAsync: ct2 => catalogWriter.CreateTrackedGroupedProductAsync(
                    newGroupName, name,
                    defaultUnitId:    unitId,
                    categoryId:       payload.CategoryId,
                    defaultLocationId: LocationId,
                    ct2),
                countedValue: payload.CountedValue,
                countUnit:    countUnit,
                userId:       userId,
                ct:           ct);
        }
        else
        {
            // Path C: standalone product — delegate to the existing AddCountedItemCommand.
            // Category is forwarded so the user's Defaults-collapsible choice persists (plantry-l92u).
            var cmd = new AddCountedItemCommand(
                name, unitId, LocationId,
                payload.CountedValue, countUnit,
                userId, catalogWriter, stocks, conversions, clock, tenant,
                categoryId: payload.CategoryId);
            result = await cmd.ExecuteAsync(ct);
        }

        if (result.IsFailure)
        {
            logger.LogWarning(
                "AddCountedItem failed for product '{ProductName}' at location {LocationId}: {ErrorCode}.",
                payload.Name.Trim(), LocationId, result.Error.Code);
            return new JsonResult(new { isSuccess = false, error = result.Error.Description });
        }

        var productId = result.Value;

        // Resolve the unit code for the response — the client needs it to display the row.
        //
        // The row must carry the unit the opening balance was ACTUALLY recorded in (countUnit),
        // not the product default unit (plantry-8hic). RecordCountCommand persisted the opening lot
        // in countUnit, and the client seeds the injected row dirty (recorded 0, counted = value)
        // then re-saves it posting countedUnitId = row.unitId. If the row carried the default unit
        // while it differs from countUnit, the re-save would recompute the recorded sum in the
        // default unit: with no conversion path it fails ('Failed to save' on the just-added
        // product); with a global mass conversion it would treat the counted value as being in the
        // default unit and post a large erroneous delta. Returning countUnit makes the re-saved
        // unit match the recorded lot, so RecordCountCommand's NoOp idempotency holds regardless of
        // whether the user chose an opening-count unit distinct from the product default.
        var units = await unitRepository.ListAsync(ct);
        var unitCode = units.FirstOrDefault(u => u.Id.Value == countUnit)?.Code ?? "?";

        return new JsonResult(new
        {
            isSuccess = true,
            productId,
            productName = payload.Name.Trim(),
            unitCode,
            unitId = countUnit,
            countedValue = payload.CountedValue,
        });
    }

    // ── POST (Save) ───────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts a JSON body <c>{ items: [{ productId, countedValue, countedUnitId, reason }] }</c>
    /// posted by the Alpine save function. Runs <see cref="SaveCountsCommand"/> and returns a
    /// per-item result so the client can reflect partial failures without a page reload (TS-6 / C7).
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        SaveRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SaveRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload?.Items is null)
            return BadRequest(new { error = "No items supplied." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;

        // Defense-in-depth: skip items where countedUnitId is missing, empty string, or Guid.Empty
        // rather than letting System.Text.Json throw (non-parseable string) or the command fail the
        // whole batch. SaveItem.CountedUnitId is typed as string? so that an empty-string payload
        // ("countedUnitId": "") from stale Path A clients deserializes cleanly instead of throwing.
        // The root cause (missing DefaultUnitId in Path A) is fixed in selectProduct() and the
        // search result projection, but this guard defends against any remaining stale clients.
        var invalidItems = new List<SaveItem>();
        var validItems = new List<CountItem>();

        // NeedsConversion backstop (plantry-3mwx) — the exact analogue of Recipes' C10 flow
        // (AuthorRecipe R7/C10). A counted unit that has no conversion path to the product's default
        // unit must NOT be silently recorded (previously the count was applied in whichever unit the
        // client sent, or the client had already dropped it to the product default). Instead we
        // surface a per-row prompt so the user supplies a conversion factor via OnPostAddConversion,
        // then re-saves. Unit codes are resolved lazily (only when a row actually needs conversion).
        var needsConversionResults = new List<object>();
        Dictionary<Guid, string>? unitCodesById = null;

        foreach (var i in payload.Items)
        {
            if (!Guid.TryParse(i.CountedUnitId, out var unitId) || unitId == Guid.Empty)
            {
                invalidItems.Add(i);
                continue;
            }

            // Resolve the product's default unit; if the counted unit differs and there is no
            // conversion path to the default unit, hold the row for a conversion factor. When the
            // product cannot be resolved we fall through to the command (which fails loudly on an
            // unresolvable unit as the backstop) rather than guessing.
            var product = await productRepository.FindAsync(ProductId.From(i.ProductId), ct);
            if (product is not null && unitId != product.DefaultUnitId.Value)
            {
                var defaultUnitId = product.DefaultUnitId.Value;
                var converter = await conversions.ForProductAsync(i.ProductId, ct);
                if (converter.Convert(1m, unitId, defaultUnitId).IsFailure)
                {
                    unitCodesById ??= (await unitRepository.ListAsync(ct))
                        .ToDictionary(u => u.Id.Value, u => u.Code);

                    needsConversionResults.Add(new
                    {
                        ProductId = i.ProductId,
                        IsSuccess = false,
                        needsConversion = true,
                        fromUnitId = unitId,
                        fromUnitCode = unitCodesById.GetValueOrDefault(unitId, "?"),
                        toUnitId = defaultUnitId,
                        toUnitCode = unitCodesById.GetValueOrDefault(defaultUnitId, "?"),
                        error = "This unit needs a conversion factor before it can be recorded.",
                    });
                    continue;
                }
            }

            validItems.Add(new CountItem(
                i.ProductId,
                LocationId,
                i.CountedValue,
                unitId,
                ParseReason(i.Reason)));
        }

        // Build per-row results — invalid items get an inline error, valid items go through the command.
        var perRowResults = new List<object>();

        foreach (var invalid in invalidItems)
        {
            perRowResults.Add(new
            {
                ProductId = invalid.ProductId,
                IsSuccess = false,
                error = "Unit is required — please select a unit before saving.",
            });
        }

        perRowResults.AddRange(needsConversionResults);

        if (validItems.Count > 0)
        {
            var cmd = new SaveCountsCommand(validItems, userId, stocks, conversions, clock, tenant);
            var result = await cmd.ExecuteAsync(ct);

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "SaveCounts failed at location {LocationId}: {ErrorCode}.", LocationId, result.Error.Code);
                return StatusCode(500, new { error = result.Error.Description });
            }

            perRowResults.AddRange(result.Value.Select(r => (object)new
            {
                r.ProductId,
                r.IsSuccess,
                error = r.IsSuccess ? null : r.FailureReason?.Description,
            }));
        }

        return new JsonResult(new { results = perRowResults });
    }

    // ── POST (Add conversion — NeedsConversion backstop, plantry-3mwx) ─────────

    /// <summary>
    /// Accepts a JSON body <c>{ productId, fromUnitId, toUnitId, factor }</c> posted by the island's
    /// inline conversion-factor prompt when a Save returned a <c>needsConversion</c> row. Persists the
    /// product-specific conversion via <see cref="ITakeStockCatalogWriter.AddConversionAsync"/> (over
    /// Catalog's <c>AddConversionCommand</c>) so the subsequent re-save can convert the counted unit
    /// into the product's default unit. Returns <c>{ isSuccess, error? }</c> — the mirror of the
    /// Recipes C10 post-save conversion flow.
    /// </summary>
    public async Task<IActionResult> OnPostAddConversionAsync(CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        AddConversionRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AddConversionRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload is null
            || payload.ProductId == Guid.Empty
            || payload.FromUnitId == Guid.Empty
            || payload.ToUnitId == Guid.Empty)
            return BadRequest(new { error = "productId, fromUnitId, and toUnitId are required." });

        if (payload.Factor <= 0m)
            return new JsonResult(new { isSuccess = false, error = "Enter a conversion factor greater than zero." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        try
        {
            await catalogWriter.AddConversionAsync(
                payload.ProductId, payload.FromUnitId, payload.ToUnitId, payload.Factor, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "AddConversion failed for product {ProductId} ({FromUnitId}→{ToUnitId}) at location {LocationId}: {Message}.",
                payload.ProductId, payload.FromUnitId, payload.ToUnitId, LocationId, ex.Message);
            return new JsonResult(new { isSuccess = false, error = ex.Message });
        }

        return new JsonResult(new { isSuccess = true });
    }

    // ── GET (Lots fragment — escape hatch) ────────────────────────────────────

    /// <summary>
    /// Returns the lot-panel fragment for a single (product, location) pair (P4-5, J3).
    /// Called via htmx GET when the user expands a count row's lot list.
    /// Renders <c>_LotPanel</c> partial pre-populated with active lots from
    /// <see cref="ITakeStockReader.ListLotsAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnGetLotsAsync(Guid productId, CancellationToken ct = default)
    {
        var lots = await reader.ListLotsAsync(productId, LocationId, ct);
        return Partial("_LotPanel", new LotPanelModel(productId, LocationId, lots));
    }

    // ── POST (SaveLots — escape-hatch adjustments) ────────────────────────────

    /// <summary>
    /// Accepts a JSON body of per-lot adjustments (P4-5, J3):
    /// <c>{ adjustments: [{ entryId?, amount, unitId, reason, expiryDate? }] }</c>.
    /// Runs <see cref="SaveLotAdjustmentsCommand"/> and returns a per-item result vector.
    /// </summary>
    public async Task<IActionResult> OnPostSaveLotsAsync(Guid productId, CancellationToken ct = default)
    {
        using var bodyReader = new StreamReader(Request.Body);
        var bodyJson = await bodyReader.ReadToEndAsync(ct);
        SaveLotsRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SaveLotsRequest>(bodyJson, JsonOptions);
        }
        catch
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (payload?.Adjustments is null)
            return BadRequest(new { error = "No adjustments supplied." });

        if (tenant.HouseholdId is null)
            return Unauthorized();

        var userId = CurrentUserId;
        var adjustments = payload.Adjustments
            .Select(a => new LotAdjustItem(
                a.EntryId,
                a.Amount,
                a.UnitId,
                ParseReason(a.Reason),
                a.ExpiryDate))
            .ToList();

        var cmd = new SaveLotAdjustmentsCommand(
            productId, LocationId, adjustments, userId, stocks, conversions, clock, tenant);
        var result = await cmd.ExecuteAsync(ct);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "SaveLotAdjustments failed for product {ProductId} at location {LocationId}: {ErrorCode}.",
                productId, LocationId, result.Error.Code);
            return StatusCode(500, new { error = result.Error.Description });
        }

        var outcome = result.Value;
        if (!outcome.IsSuccess && outcome.Results.Count == 0)
        {
            logger.LogWarning(
                "SaveLotAdjustments batch-level failure for product {ProductId} at location {LocationId}: {ErrorCode}.",
                productId, LocationId, outcome.FailureReason?.Code);
            return StatusCode(500, new { error = outcome.FailureReason?.Description });
        }

        var responseItems = outcome.Results.Select(r => new
        {
            r.EntryId,
            r.IsSuccess,
            error = r.IsSuccess ? null : r.FailureReason?.Description,
        });

        return new JsonResult(new { results = responseItems });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared "create then count" helper for the group-aware create paths (Paths A/B in
    /// <see cref="OnPostAddItemAsync"/>). Calls the catalog writer, then runs
    /// <see cref="RecordCountCommand"/> for a positive opening balance, mirroring
    /// <see cref="AddCountedItemCommand.ExecuteAsync"/> for Paths A/B.
    /// </summary>
    private async Task<Result<Guid>> CreateAndCountAsync(
        Func<CancellationToken, Task<Guid>> createAsync,
        decimal countedValue,
        Guid countUnit,
        Guid userId,
        CancellationToken ct)
    {
        Guid productId;
        try
        {
            productId = await createAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Custom("Inventory.InlineAddFailed", ex.Message);
        }

        if (countedValue > 0m)
        {
            var countCmd = new RecordCountCommand(
                productId, LocationId, countedValue, countUnit,
                StockReason.Correction, userId, stocks, conversions, clock, tenant);

            var countResult = await countCmd.ExecuteAsync(ct);
            if (countResult.IsFailure)
                return countResult.Error;
        }

        return productId;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var locations = await reader.ListLocationsAsync(ct);
        LocationName = locations.FirstOrDefault(l => l.LocationId == LocationId)?.LocationName;
        Rows = await reader.ListLocationRowsAsync(LocationId, ct);
        IslandRowsJson = BuildIslandRowsJson(Rows);

        // Load unit options for the inline-add sheet (P4-7).
        var units = await unitRepository.ListAsync(ct);
        UnitOptions = units
            .OrderBy(u => u.Code, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SelectListItem(u.Code, u.Id.Value.ToString()))
            .ToList();

        // Load group options for the create-view Group combobox (plantry-40n6).
        // Groups are active products with HasVariants = true. Filtered client-side in Alpine.
        var allProducts = await productRepository.ListActiveAsync(ct);
        GroupOptions = allProducts
            .Where(p => p.IsParent)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new GroupOption(p.Id.Value.ToString(), p.Name))
            .ToList();

        // Load category options for the Defaults collapsible in the create view (plantry-y53t).
        CategoryOptions = (await categoryRepository.ListActiveAsync(ct))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new SelectListItem(c.Name, c.Id.Value.ToString()))
            .ToList();
    }

    /// <summary>
    /// Builds the richer hydration JSON array for the Preact island. The island renders the whole
    /// row (product name, recorded, supported units, lots URL) so the payload must carry those fields
    /// in addition to the mutable draft state (counted / unitId / reason) that the Alpine version held.
    /// </summary>
    private string BuildIslandRowsJson(IReadOnlyList<TakeStockLocationProductRow> rows)
    {
        var list = rows.Select(r => new IslandRowVm(
            r.ProductId,
            r.ProductName,
            r.RecordedQuantity,
            r.DisplayUnitCode,
            r.DisplayUnitId,
            r.HasActiveStock,
            // lotsUrl is resolved server-side so the island never constructs URLs.
            // The handler remains the sole owner of its own routing.
            Url.Page("./Walk", "Lots", new { locationId = LocationId, productId = r.ProductId }) ?? "",
            r.SupportedUnits?
                .Select(u => new UnitOptionVm(u.UnitId, u.Code))
                .ToList() ?? []));

        return JsonSerializer.Serialize(list, TakeStockHydrationJson.Options);
    }

    private static StockReason ParseReason(string? reason) => reason switch
    {
        "Consumed"  => StockReason.Consumed,
        "Discarded" => StockReason.Discarded,
        _           => StockReason.Correction,
    };

    // Defensive parse: under [Authorize] the NameIdentifier claim is always present, but a missing/
    // malformed claim degrades to Guid.Empty (the same "no principal" sentinel the household id uses)
    // rather than throwing a 500.
    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class SaveRequest
    {
        public List<SaveItem>? Items { get; set; }
    }

    private sealed class SaveItem
    {
        public Guid    ProductId     { get; set; }
        public decimal CountedValue  { get; set; }
        /// <summary>
        /// Stored as string? so JSON deserialization tolerates an empty string ("") from stale
        /// Path A clients that posted unitId before DefaultUnitId was wired up.
        /// Parsed to Guid (with empty-string → Guid.Empty → per-row error) in OnPostSaveAsync.
        /// </summary>
        public string? CountedUnitId { get; set; }
        public string? Reason        { get; set; }
    }

    private sealed class SaveLotsRequest
    {
        public List<SaveLotAdjust>? Adjustments { get; set; }
    }

    /// <summary>
    /// Body for <see cref="OnPostAddConversionAsync"/> (plantry-3mwx). The factor is stored in the
    /// <see cref="FromUnitId"/>→<see cref="ToUnitId"/> direction (i.e. "1 fromUnit = factor toUnit"),
    /// where fromUnit is the counted unit and toUnit is the product's default unit.
    /// </summary>
    private sealed class AddConversionRequest
    {
        public Guid    ProductId  { get; set; }
        public Guid    FromUnitId { get; set; }
        public Guid    ToUnitId   { get; set; }
        public decimal Factor     { get; set; }
    }

    private sealed class SaveLotAdjust
    {
        public Guid?    EntryId    { get; set; }
        public decimal  Amount     { get; set; }
        public Guid     UnitId     { get; set; }
        public string?  Reason     { get; set; }
        public DateOnly? ExpiryDate { get; set; }
    }

    private sealed class AddItemRequest
    {
        /// <summary>Product name typed in the create-new mode of the inline-add sheet.</summary>
        public string? Name           { get; set; }
        /// <summary>Selected default unit for the new product (from the Defaults collapsible, plantry-y53t).</summary>
        public Guid   DefaultUnitId   { get; set; }
        /// <summary>Counted quantity for the opening-balance Correction (0 = register only, no stock).</summary>
        public decimal CountedValue   { get; set; }
        /// <summary>Unit for the counted quantity; falls back to DefaultUnitId when empty.</summary>
        public Guid   CountedUnitId   { get; set; }

        // ── Group-aware create fields (plantry-l92u) ──────────────────────────
        /// <summary>
        /// Non-empty string = join an existing group (CreateVariantCommand).
        /// Empty string or null = no group join (check NewGroupName for new-group path).
        /// </summary>
        public string? NewGroupId    { get; set; }
        /// <summary>
        /// Non-empty string when NewGroupId is empty = create a new group with this name
        /// (CreateGroupedProductCommand). Empty string or null = standalone product.
        /// </summary>
        public string? NewGroupName  { get; set; }
        /// <summary>Optional category for the new product (from the Defaults collapsible).</summary>
        public Guid?  CategoryId     { get; set; }
    }
}

/// <summary>
/// View model for the <c>_LotPanel</c> partial (P4-5, J3).
/// Carries the active lots for one (product, location) pair to render the escape-hatch lot list.
/// </summary>
public sealed record LotPanelModel(
    Guid ProductId,
    Guid LocationId,
    IReadOnlyList<TakeStockLotRow> Lots);
