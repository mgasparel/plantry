using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Infrastructure;

/// <summary>
/// EF-backed implementation of <see cref="IShoppingListRepository"/>.
/// All queries use the context's query filter (household scoping) + RLS backstop.
/// Items are loaded eagerly so the aggregate is always in a consistent state.
/// </summary>
public sealed class ShoppingListRepository(ShoppingDbContext db) : IShoppingListRepository
{
    public async Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        await db.ShoppingLists
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.HouseholdId == householdId, ct);

    public async Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
        await db.ShoppingLists
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task AddAsync(ShoppingList list, CancellationToken ct = default) =>
        await db.ShoppingLists.AddAsync(list, ct);

    public Task SaveAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
