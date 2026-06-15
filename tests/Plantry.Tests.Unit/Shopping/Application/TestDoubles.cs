using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

/// <summary>
/// In-memory implementation of <see cref="IShoppingListRepository"/>.
/// One list per household (v1 invariant). Seeded by tests via <see cref="Seed"/>.
/// </summary>
internal sealed class FakeShoppingListRepository : IShoppingListRepository
{
    private readonly List<ShoppingList> _lists = [];
    public int SaveCalls { get; private set; }

    /// <summary>Inserts a pre-built list so tests can start from a known state.</summary>
    public void Seed(ShoppingList list) => _lists.Add(list);

    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(_lists.FirstOrDefault(l => l.HouseholdId == householdId));

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
        Task.FromResult(_lists.FirstOrDefault(l => l.Id == id));

    public Task AddAsync(ShoppingList list, CancellationToken ct = default)
    {
        _lists.Add(list);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.CompletedTask;
    }
}
