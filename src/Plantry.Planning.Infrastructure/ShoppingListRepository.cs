using Microsoft.EntityFrameworkCore;
using Plantry.SharedKernel;
using Plantry.Planning.Domain;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// EF-backed implementation of <see cref="IShoppingListRepository"/>.
/// All queries use the context's query filter (household scoping) + RLS backstop.
/// Items and their contributions are loaded eagerly so the aggregate is always in a consistent
/// state and per-source upsert logic can read the existing contributions (plantry-9scq).
/// </summary>
public sealed class ShoppingListRepository(PlanningDbContext db) : IShoppingListRepository
{
    public async Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        await db.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstOrDefaultAsync(l => l.HouseholdId == householdId, ct);

    public async Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
        await db.ShoppingLists
            .Include(l => l.Items)
            .ThenInclude(i => i.Contributions)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task AddAsync(ShoppingList list, CancellationToken ct = default) =>
        await db.ShoppingLists.AddAsync(list, ct);

    public Task SaveAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
