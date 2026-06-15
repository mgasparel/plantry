using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Shopping.Domain;

namespace Plantry.Shopping.Infrastructure;

/// <summary>
/// Implements <see cref="IReferenceDataSeeder"/> for the Shopping context.
/// Seeds exactly ONE <see cref="ShoppingList"/> per household on creation (shopping.md resolved call 1,
/// DM-18). The list name defaults to "Shopping List"; additional named lists are a non-breaking
/// future addition (the root table and Name column exist for that reason).
/// </summary>
public sealed class ShoppingReferenceDataSeeder(ShoppingDbContext db, IClock clock) : IReferenceDataSeeder
{
    public async Task SeedAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var list = ShoppingList.Create(householdId, clock);
        await db.ShoppingLists.AddAsync(list, ct);
        await db.SaveChangesAsync(ct);
    }
}
