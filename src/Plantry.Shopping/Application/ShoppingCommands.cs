using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Application;

/// <summary>
/// Adds a single item to the household's shopping list (SPEC §3b, shopping.md resolved calls 4/5).
///
/// <para><b>Product-backed items (productId set):</b> the MERGE rule applies — if an unchecked
/// item for the same product already exists, the incoming quantity is merged into it rather than
/// inserting a duplicate.  Set <paramref name="intentionalDuplicate"/> to <c>true</c> to bypass
/// the merge and force a second line (e.g. buying the same product from two different stores).
/// There is no DB unique constraint; the constraint is entirely app-layer intent.</para>
///
/// <para><b>Unit mismatch on merge (plantry-xw6):</b> when the existing and incoming units differ,
/// the command attempts to convert the incoming quantity to the existing item's unit via
/// <paramref name="catalogReader"/>. If conversion succeeds the merged sum is expressed in the
/// existing unit. If conversion is not possible (cross-dimension with no product conversion, or
/// exactly one side has no unit) a second line is inserted instead of silently summing meaningless
/// quantities.</para>
///
/// <para><b>Free-text items (freeText set):</b> no merge — free-text items are always inserted.</para>
///
/// <para>Exactly one of <paramref name="productId"/> / <paramref name="freeText"/> must be set;
/// the DB CHECK backs this up but it is enforced at the application layer first.</para>
///
/// <para>This merge primitive is the shared seam P2-4 will reuse when bulk-adding from a recipe
/// (source=recipe path added in P2-4 via a separate bulk overload).</para>
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
    ITenantContext tenant)
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
            return Error.Custom("Shopping.NoList", "No shopping list found for this household.");

        ShoppingListItem item;

        if (productId.HasValue)
        {
            // Apply merge rule: if an unchecked item for this product already exists and the
            // caller has not flagged this as an intentional second add, merge quantities.
            if (!intentionalDuplicate)
            {
                var existing = list.FindUncheckedByProduct(productId.Value);
                if (existing is not null)
                {
                    // Determine whether the units are compatible for merging.
                    // Policy (plantry-xw6):
                    //   1. Units match (or both null) → merge by summing in the existing unit.
                    //   2. Units differ, both non-null, conversion exists → convert incoming to
                    //      existing unit, then merge (sum) in that unit.
                    //   3. Units differ, both non-null, no conversion → insert a second line.
                    //   4. Exactly one side has a unit (null vs. non-null) → insert a second line.
                    bool unitsCompatible = existing.UnitId == unitId; // covers both-null and equal case

                    if (!unitsCompatible)
                    {
                        // Both units must be non-null and convertible for a merge to proceed.
                        if (existing.UnitId.HasValue && unitId.HasValue && quantity.HasValue)
                        {
                            var converted = await catalogReader.TryConvertAsync(
                                quantity.Value, unitId.Value, existing.UnitId.Value, productId.Value, ct);

                            if (converted.HasValue)
                            {
                                // Conversion succeeded: merge using the converted amount in the existing unit.
                                list.MergeItem(existing, converted.Value, existing.UnitId, clock);
                                await repository.SaveAsync(ct);
                                return existing.Id;
                            }
                            // No conversion path → fall through to insert a second line.
                        }
                        // One or both sides have no unit, or quantity is null → fall through to insert a second line.
                    }
                    else
                    {
                        // Units match (or both null): merge by summing as before.
                        list.MergeItem(existing, quantity, unitId, clock);
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
/// Checks off a shopping list item: stamps <c>checked_at</c> and <c>checked_by</c> (SPEC §3c).
/// Idempotent — checking off an already-checked item re-stamps the time.
/// </summary>
public sealed class CheckOffCommand(
    ShoppingListId listId,
    ShoppingListItemId itemId,
    Guid userId,
    IShoppingListRepository repository,
    IClock clock,
    ITenantContext tenant)
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
            return Error.Unauthorized;

        try
        {
            list.CheckOff(itemId, userId, clock);
        }
        catch (InvalidOperationException)
        {
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
    ITenantContext tenant)
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
            return Error.Unauthorized;

        try
        {
            list.UncheckItem(itemId, clock);
        }
        catch (InvalidOperationException)
        {
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
    ITenantContext tenant)
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
            return Error.Unauthorized;

        try
        {
            list.RemoveItem(itemId, clock);
        }
        catch (InvalidOperationException)
        {
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
    ITenantContext tenant)
{
    public async Task<Result<int>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        var household = HouseholdId.From(householdId);

        var list = await repository.GetForHouseholdAsync(household, ct);
        if (list is null)
            return Error.Custom("Shopping.NoList", "No shopping list found for this household.");

        var cleared = list.ClearChecked(clock);
        if (cleared.Count > 0)
            await repository.SaveAsync(ct);

        return cleared.Count;
    }
}
