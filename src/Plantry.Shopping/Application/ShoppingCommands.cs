using Microsoft.Extensions.Logging;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Application;

/// <summary>
/// Adds a single item to the household's shopping list (SPEC §3b, shopping.md resolved calls 4/5).
///
/// <para><b>Product-backed items (productId set):</b> if an unchecked item for the same product
/// already exists (and the call is not flagged as an intentional duplicate), the command upserts
/// a per-source contribution using (Source, SourceRef) as the match key (plantry-9scq).
///
/// <list type="bullet">
///   <item><description>Same (Source, SourceRef) pair → top up that source's contribution by
///     delta = max(0, incoming − that source's current quantity). Re-adding an identical shortfall
///     is a no-op (idempotent per source).</description></item>
///   <item><description>New (Source, SourceRef) pair → add a fresh contribution; the item's total
///     Quantity (SUM of all contributions) grows by the new amount.</description></item>
/// </list>
///
/// Two distinct SourceRefs for the same recipe (e.g. meal-plan Mon+Thu entries) produce two
/// separate contributions that SUM — they must NOT be collapsed to the recipeId.</para>
///
/// <para><b>Unit mismatch on upsert (plantry-xw6 — preserved):</b> when the existing item's unit
/// and the incoming unit differ, the command attempts to convert the incoming quantity to the
/// existing item's unit via <paramref name="catalogReader"/>. If conversion succeeds the
/// per-source upsert proceeds in the existing unit. If conversion is not possible (cross-dimension
/// or one side is null), a second item row is inserted instead.</para>
///
/// <para><b>Free-text items (freeText set):</b> always inserted unconditionally — no per-source merge.
/// Free-text items have exactly one Manual contribution.</para>
///
/// <para>Exactly one of <paramref name="productId"/> / <paramref name="freeText"/> must be set;
/// the DB CHECK backs this up but it is enforced at the application layer first.</para>
/// </summary>
public sealed class AddItemCommand(
    Guid? productId,
    string? freeText,
    decimal? quantity,
    Guid? unitId,
    string? note,
    ItemSource source,
    Guid? sourceRef,
    bool intentionalDuplicate,
    IShoppingListRepository repository,
    IShoppingCatalogReader catalogReader,
    IClock clock,
    ITenantContext tenant,
    ILogger<AddItemCommand>? logger = null)
{
    public async Task<Result<ShoppingListItemId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        // Enforce exactly-one-of at the app layer (mirrors the DB CHECK).
        if (productId.HasValue == (freeText is not null))
            return Error.Custom("Shopping.InvalidItem",
                "Exactly one of productId or freeText must be supplied, not both or neither.");

        var household = HouseholdId.From(householdId);

        var list = await repository.GetForHouseholdAsync(household, ct);
        if (list is null)
        {
            logger?.LogWarning("AddItem failed — no shopping list found for household {HouseholdId}.", householdId);
            return Error.Custom("Shopping.NoList", "No shopping list found for this household.");
        }

        ShoppingListItem item;

        if (productId.HasValue)
        {
            // Per-source contribution model (plantry-9scq): if an unchecked item for this product
            // already exists and the caller has not flagged this as an intentional duplicate,
            // upsert a contribution into the existing row using (source, sourceRef) as the key.
            if (!intentionalDuplicate)
            {
                var existing = list.FindUncheckedByProduct(productId.Value);
                if (existing is not null)
                {
                    // Unit gate (plantry-xw6 — preserved):
                    //   1. Units match (or both null) → proceed with per-source upsert.
                    //   2. Units differ, both non-null, conversion exists → convert to existing unit, then upsert.
                    //   3. Units differ, both non-null, no conversion → insert a second line.
                    //   4. Exactly one side has no unit → insert a second line.
                    bool unitsCompatible = existing.UnitId == unitId; // covers both-null and equal case

                    if (!unitsCompatible)
                    {
                        // Both units must be non-null and convertible for an upsert into the existing row.
                        if (existing.UnitId.HasValue && unitId.HasValue && quantity.HasValue)
                        {
                            var converted = await catalogReader.TryConvertAsync(
                                quantity.Value, unitId.Value, existing.UnitId.Value, productId.Value, ct);

                            if (converted.HasValue)
                            {
                                // Conversion succeeded: upsert using the converted amount in the existing unit.
                                list.UpsertContribution(existing, source, sourceRef, converted.Value, existing.UnitId, clock);
                                await repository.SaveAsync(ct);
                                return existing.Id;
                            }
                            // No conversion path → fall through to insert a second item row.
                        }
                        // One or both sides have no unit, or quantity is null → fall through to insert a second item row.
                    }
                    else
                    {
                        // Units match (or both null): per-source upsert into the existing row.
                        list.UpsertContribution(existing, source, sourceRef, quantity, unitId, clock);
                        await repository.SaveAsync(ct);
                        return existing.Id;
                    }
                }
            }

            item = list.AddItem(productId.Value, quantity, unitId, note, source, sourceRef, clock);
        }
        else
        {
            // freeText is not null here — enforced by the XOR check above.
            item = list.AddFreeTextItem(freeText!, quantity, unitId, note, clock);
        }

        await repository.SaveAsync(ct);
        return item.Id;
    }
}

/// <summary>
/// Idempotently SYNCS a single source's contribution slice across the household's shopping list to
/// exactly the supplied target set (plantry-gsj). This is the SET verb behind the recipe Detail
/// "Add missing" / "Add all" buttons, as opposed to the additive <see cref="AddItemCommand"/> merge.
///
/// <para>For the given (<paramref name="source"/>, <paramref name="sourceRef"/>):</para>
/// <list type="number">
///   <item><description><b>Reconcile-remove:</b> every unchecked item carrying this source's slice
///     whose product is NOT in the target set has that slice removed (a row left with zero
///     contributions is deleted). This is what makes "Add all" then "Add missing" last-press-wins —
///     the in-stock products Add-all placed are dropped when Add-missing re-syncs to the shortfall.</description></item>
///   <item><description><b>Set each target:</b> the source's slice on the product's unchecked row is
///     SET (created or replaced, never incremented) to the target quantity. Re-pressing at the same
///     servings is a no-op; a servings increase tops the slice up to the new shortfall only. Other
///     sources' slices on the row are untouched (plantry-9scq sums / plantry-26g attribution kept).</description></item>
///   <item><description><b>No resurrection:</b> a target product with no unchecked row but a
///     checked-off row is skipped (counted as "checked off"), never re-added.</description></item>
/// </list>
///
/// <para>Unit handling mirrors <see cref="AddItemCommand"/>: a target whose unit differs from the
/// existing unchecked row is converted via <paramref name="catalogReader"/> when possible, else a
/// separate row is inserted (the recipe's stable per-product unit means this edge is rare).</para>
///
/// <para>Returns a <see cref="SyncSourceOutcome"/> of per-target counts for the result summary.</para>
/// </summary>
public sealed class SyncSourceContributionCommand(
    IReadOnlyList<SyncItem> items,
    ItemSource source,
    Guid sourceRef,
    IShoppingListRepository repository,
    IShoppingCatalogReader catalogReader,
    IClock clock,
    ITenantContext tenant,
    ILogger<SyncSourceContributionCommand>? logger = null)
{
    public async Task<Result<SyncSourceOutcome>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetForHouseholdAsync(HouseholdId.From(householdId), ct);
        if (list is null)
        {
            logger?.LogWarning("SyncSourceContribution failed — no shopping list found for household {HouseholdId}.", householdId);
            return Error.Custom("Shopping.NoList", "No shopping list found for this household.");
        }

        var targetProductIds = items.Select(i => i.ProductId).ToHashSet();

        // ── Phase 1: reconcile-remove ────────────────────────────────────────────
        // Drop this source's slice from any unchecked row whose product is no longer targeted
        // (enables Add-all → Add-missing last-press-wins). Snapshot first — RemoveSourceContribution
        // may delete the row from the collection being enumerated.
        var stale = list.Items
            .Where(i => !i.IsChecked
                        && i.ProductId.HasValue
                        && !targetProductIds.Contains(i.ProductId.Value)
                        && i.FindContribution(source, sourceRef) is not null)
            .ToList();
        foreach (var item in stale)
            list.RemoveSourceContribution(item, source, sourceRef, clock);

        // ── Phase 2: set each target ─────────────────────────────────────────────
        var added = 0;
        var alreadyPresent = 0;
        var checkedOff = 0;

        foreach (var target in items)
        {
            // Prefer the row already holding this source's slice (idempotent across unit splits),
            // then any unchecked row for the product.
            var row = list.FindUncheckedRowForSource(target.ProductId, source, sourceRef)
                      ?? list.FindUncheckedByProduct(target.ProductId);

            if (row is not null)
            {
                // Unit gate (mirrors AddItemCommand): same unit → set directly; convertible → set in
                // the row's unit; otherwise fall through to a separate row.
                if (row.UnitId == target.UnitId)
                {
                    Tally(list.SetSourceContribution(row, source, sourceRef, target.Quantity, target.UnitId, clock));
                    continue;
                }

                if (row.UnitId.HasValue && target.UnitId.HasValue && target.Quantity.HasValue)
                {
                    var converted = await catalogReader.TryConvertAsync(
                        target.Quantity.Value, target.UnitId.Value, row.UnitId.Value, target.ProductId, ct);
                    if (converted.HasValue)
                    {
                        Tally(list.SetSourceContribution(row, source, sourceRef, converted.Value, row.UnitId, clock));
                        continue;
                    }
                    // No conversion path → insert a separate row below.
                }
                // One side unitless, or no conversion → insert a separate row below.
            }
            else if (list.HasCheckedItemForProduct(target.ProductId))
            {
                // No unchecked row, but a checked-off completed intent exists — do not resurrect it.
                checkedOff++;
                continue;
            }

            // No usable row (or a unit-incompatible split) → create a fresh row seeded with this slice.
            list.AddItem(target.ProductId, target.Quantity, target.UnitId, note: null, source, sourceRef, clock);
            added++;
        }

        await repository.SaveAsync(ct);

        logger?.LogInformation(
            "Synced {Source} contribution {SourceRef} for household {HouseholdId}: {Added} added, {AlreadyPresent} already present, {CheckedOff} checked-off skipped.",
            source, sourceRef, householdId, added, alreadyPresent, checkedOff);

        return new SyncSourceOutcome(added, alreadyPresent, checkedOff);

        void Tally(ContributionChange change)
        {
            if (change is ContributionChange.Created or ContributionChange.Increased)
                added++;
            else
                alreadyPresent++;
        }
    }
}

/// <summary>One target line for <see cref="SyncSourceContributionCommand"/> (plantry-gsj).</summary>
/// <param name="ProductId">Soft ref → catalog.product.</param>
/// <param name="Quantity">The absolute quantity this source's slice should be SET to, in <paramref name="UnitId"/>.</param>
/// <param name="UnitId">Soft ref → catalog.unit.</param>
public sealed record SyncItem(Guid ProductId, decimal? Quantity, Guid? UnitId);

/// <summary>
/// Per-target counts from a <see cref="SyncSourceContributionCommand"/> (plantry-gsj), surfaced to
/// the user as "Added X · Y already on your list · Z checked off".
/// </summary>
/// <param name="Added">Targets whose slice was created or grown.</param>
/// <param name="AlreadyPresent">Targets whose slice already covered the shortfall (no change).</param>
/// <param name="CheckedOff">Targets skipped because only a checked-off row exists (no resurrection).</param>
public sealed record SyncSourceOutcome(int Added, int AlreadyPresent, int CheckedOff);

/// <summary>
/// Checks off a shopping list item: stamps <c>checked_at</c> and <c>checked_by</c> (SPEC §3c).
/// Idempotent — checking off an already-checked item re-stamps the time.
/// </summary>
public sealed class CheckOffCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    Guid userId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<CheckOffCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — the EF query filter already enforces this, but we assert it explicitly
        // for clarity (mirrors the pattern in other command handlers).
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("CheckOff rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.CheckOff(itemId, userId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "CheckOff failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Unchecks a previously checked shopping list item: clears <c>checked_at</c> and <c>checked_by</c>.
/// Idempotent — unchecking an already-unchecked item is a no-op (just re-stamps updated_at).
/// </summary>
public sealed class UncheckItemCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<UncheckItemCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — defense-in-depth alongside EF query filter.
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("UncheckItem rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.UncheckItem(itemId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "UncheckItem failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Hard-deletes a single item from the household's shopping list.
/// Distinct from <see cref="ClearCheckedCommand"/> — removes any item regardless of checked state.
/// </summary>
public sealed class DeleteItemCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<DeleteItemCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — defense-in-depth alongside EF query filter.
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("DeleteItem rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.RemoveItem(itemId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "DeleteItem failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Edits the quantity and unit of a single shopping list item in place (plantry-dem).
/// The item must belong to the household's current list. Tenancy is enforced via
/// the list's <c>HouseholdId</c> compared to the tenant context — defense-in-depth
/// alongside the EF query filter.
/// </summary>
public sealed class EditQuantityCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    decimal? quantity,
    Guid? unitId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<EditQuantityCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — defense-in-depth alongside EF query filter.
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("EditQuantity rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.EditItemQuantity(itemId, quantity, unitId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "EditQuantity failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Sets or clears the note on a single shopping list item (plantry-dem).
/// Null or whitespace-only note clears the field. Tenancy enforced as defense-in-depth.
/// </summary>
public sealed class SetNoteCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    string? note,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<SetNoteCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — defense-in-depth alongside EF query filter.
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("SetNote rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.SetItemNote(itemId, note, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "SetNote failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Assigns or clears a category on a single shopping list item (recategorize action, plantry-259).
/// Only meaningful for free-text items or product-backed items whose product has no catalog category.
/// Setting categoryId to a valid Guid places the item in the named category group on the next
/// list read. Tenancy enforced as defense-in-depth alongside the EF query filter.
/// </summary>
public sealed class SetCategoryCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    Guid? categoryId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<SetCategoryCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var list = await repository.GetByIdAsync(listId, ct);
        if (list is null)
            return Error.NotFound;

        // Tenant guard — defense-in-depth alongside EF query filter.
        if (list.HouseholdId != HouseholdId.From(householdId))
        {
            logger?.LogWarning("SetCategory rejected — list {ListId} does not belong to household {HouseholdId}.", listId.Value, householdId);
            return Error.Unauthorized;
        }

        try
        {
            list.SetItemCategory(itemId, categoryId, clock);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "SetCategory failed — item {ItemId} not found on list {ListId}.", itemId.Value, listId.Value);
            return Error.NotFound;
        }

        await repository.SaveAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Hard-deletes all checked items from the household's shopping list (SPEC §3e,
/// shopping.md resolved call 2 — mutable working state, no audit trail for clears).
/// </summary>
public sealed class ClearCheckedCommand(
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant,
    ILogger<ClearCheckedCommand>? logger = null)
{
    public async Task<Result<int>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);

        var list = await repository.GetForHouseholdAsync(household, ct);
        if (list is null)
        {
            logger?.LogWarning("ClearChecked failed — no shopping list found for household {HouseholdId}.", householdId);
            return Error.Custom("Shopping.NoList", "No shopping list found for this household.");
        }

        var cleared = list.ClearChecked(clock);
        if (cleared.Count > 0)
        {
            await repository.SaveAsync(ct);
            logger?.LogInformation("Cleared {ClearedCount} checked items from shopping list for household {HouseholdId}.", cleared.Count, householdId);
        }

        return cleared.Count;
    }
}
