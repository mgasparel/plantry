using Microsoft.Extensions.Logging;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Application;

/// <summary>
/// Adds a single item to the household's shopping list (SPEC §3b, shopping.md resolved calls 4/5).
///
/// <para><b>Product-backed items (productId set):</b> the RECONCILE rule applies — if an unchecked
/// item for the same product already exists, the incoming quantity is reconciled against the current
/// list quantity so that the list always tops up to the shortfall rather than stacking (plantry-wxho).
/// Formula: <c>toAdd = max(0, incoming − alreadyOnList)</c>. A second add of the same shortfall is a
/// no-op because the deficit is already covered. This makes recipe/meal-plan "add missing" calls
/// fully idempotent within a stable stock+list state.
/// Set <paramref name="intentionalDuplicate"/> to <c>true</c> to bypass the reconcile entirely and
/// force a second line (e.g. buying the same product from two different stores).
/// There is no DB unique constraint; the constraint is entirely app-layer intent.</para>
///
/// <para><b>Unit mismatch on merge (plantry-xw6):</b> when the existing and incoming units differ,
/// the command attempts to convert the incoming quantity to the existing item's unit via
/// <paramref name="catalogReader"/>. If conversion succeeds the reconcile delta is computed in the
/// existing unit. If conversion is not possible (cross-dimension with no product conversion, or
/// exactly one side has no unit) a second line is inserted instead of silently summing meaningless
/// quantities.</para>
///
/// <para><b>Free-text items (freeText set):</b> no merge — free-text items are always inserted.</para>
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
            // Reconcile rule (plantry-wxho): if an unchecked item for this product already exists
            // and the caller has not flagged this as an intentional second add, reconcile against
            // the quantity already on the list so that the list tops up to the need rather than
            // stacking. toAdd = max(0, incoming − alreadyOnList).
            if (!intentionalDuplicate)
            {
                var existing = list.FindUncheckedByProduct(productId.Value);
                if (existing is not null)
                {
                    // Determine whether the units are compatible for merging.
                    // Policy (plantry-xw6):
                    //   1. Units match (or both null) → reconcile in the existing unit.
                    //   2. Units differ, both non-null, conversion exists → convert incoming to
                    //      existing unit, then reconcile in that unit.
                    //   3. Units differ, both non-null, no conversion → insert a second line.
                    //   4. Exactly one side has a unit (null vs. non-null) → insert a second line.
                    bool unitsCompatible = existing.UnitId == unitId; // covers both-null and equal case

                    if (!unitsCompatible)
                    {
                        // Both units must be non-null and convertible for a reconcile to proceed.
                        if (existing.UnitId.HasValue && unitId.HasValue && quantity.HasValue)
                        {
                            var converted = await catalogReader.TryConvertAsync(
                                quantity.Value, unitId.Value, existing.UnitId.Value, productId.Value, ct);

                            if (converted.HasValue)
                            {
                                // Conversion succeeded: reconcile using the converted amount in the existing unit.
                                // toAdd = max(0, convertedIncoming − alreadyOnList)
                                var toAddConverted = ComputeToAdd(converted.Value, existing.Quantity);
                                if (toAddConverted <= 0m)
                                {
                                    // Already at or above the need — no-op (idempotent).
                                    return existing.Id;
                                }
                                list.MergeItem(existing, toAddConverted, existing.UnitId, clock);
                                await repository.SaveAsync(ct);
                                return existing.Id;
                            }
                            // No conversion path → fall through to insert a second line.
                        }
                        // One or both sides have no unit, or quantity is null → fall through to insert a second line.
                    }
                    else
                    {
                        // Units match (or both null): reconcile — bump only the delta needed to
                        // reach the incoming shortfall (toAdd = max(0, incoming − alreadyOnList)).
                        var toAdd = ComputeToAdd(quantity, existing.Quantity);
                        if (toAdd <= 0m)
                        {
                            // Already at or above the need — no-op (idempotent).
                            return existing.Id;
                        }
                        list.MergeItem(existing, toAdd, unitId, clock);
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

    /// <summary>
    /// Computes the reconcile delta: max(0, incoming − alreadyOnList).
    /// Returns 0 when either value is null (nothing to add when the shortfall has no quantity).
    /// </summary>
    private static decimal ComputeToAdd(decimal? incoming, decimal? alreadyOnList)
    {
        if (!incoming.HasValue)
            return 0m;
        var already = alreadyOnList ?? 0m;
        return Math.Max(0m, incoming.Value - already);
    }
}

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
