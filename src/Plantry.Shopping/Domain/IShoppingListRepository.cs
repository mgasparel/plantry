using Plantry.SharedKernel;

namespace Plantry.Shopping.Domain;

/// <summary>
/// Repository port for the <see cref="ShoppingList"/> aggregate (shopping.md, ADR-010).
/// In v1 there is exactly one list per household; <see cref="GetForHouseholdAsync"/> is the
/// primary read path. The interface lives in the domain layer; infrastructure implements it.
/// </summary>
public interface IShoppingListRepository
{
    Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default);
    Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default);
    Task AddAsync(ShoppingList list, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
