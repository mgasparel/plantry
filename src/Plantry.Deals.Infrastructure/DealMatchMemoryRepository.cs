using Microsoft.EntityFrameworkCore;
using Plantry.Deals.Domain;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// EF-backed repository for the <see cref="DealMatchMemory"/> aggregate (P5-5). RLS-scoped by
/// <see cref="DealsDbContext"/>'s household query filter, so the <c>(store, normalized_name)</c> lookup
/// resolves only within the current household (the third component of the DD3 uniqueness key).
/// </summary>
public sealed class DealMatchMemoryRepository(DealsDbContext db) : IDealMatchMemoryRepository
{
    public Task<DealMatchMemory?> FindByKeyAsync(Guid storeId, string normalizedName, CancellationToken ct = default) =>
        db.DealMatchMemories.FirstOrDefaultAsync(
            m => m.StoreId == storeId && m.NormalizedName == normalizedName, ct);

    public async Task AddAsync(DealMatchMemory memory, CancellationToken ct = default) =>
        await db.DealMatchMemories.AddAsync(memory, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
