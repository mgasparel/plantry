using Microsoft.EntityFrameworkCore;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Inventory.Infrastructure;

/// <summary>
/// EF-backed <see cref="IHouseholdInventorySettingsRepository"/> over <see cref="InventoryDbContext"/>.
/// The context's per-household query filter plus the table's RLS policy scope every read to the
/// tenant (defence-in-depth, ADR-008).
/// </summary>
public sealed class HouseholdInventorySettingsRepository(InventoryDbContext db)
    : IHouseholdInventorySettingsRepository
{
    public Task<HouseholdInventorySettings?> FindByHouseholdAsync(
        HouseholdId householdId, CancellationToken ct = default) =>
        db.HouseholdInventorySettings
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId, ct);

    public async Task AddAsync(HouseholdInventorySettings settings, CancellationToken ct = default) =>
        await db.HouseholdInventorySettings.AddAsync(settings, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
