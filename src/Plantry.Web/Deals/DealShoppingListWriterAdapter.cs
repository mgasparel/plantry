using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for the Deals <see cref="IDealShoppingListWriter"/> — places a stock-up alert's product
/// on the household shopping list via Shopping's <see cref="AddItemCommand"/> (the reused P2-4 seam, DM-18),
/// stamping <c>source = Deal</c> and <c>sourceRef = dealId</c>. The merge rule (an unchecked item for the
/// same product is topped up per (source, sourceRef) rather than duplicated) is Shopping's responsibility —
/// enforced by <see cref="AddItemCommand"/> with <c>intentionalDuplicate = false</c>. Mirrors
/// <see cref="Plantry.Web.MealPlanning.MealPlanShoppingWriterAdapter"/>.
///
/// <para>A deal alert carries no shopping quantity of its own — it is a "buy one while it's on sale" prompt —
/// so the item is added as a single unit (<c>quantity = 1</c>) with no unit (<c>unitId = null</c>); the user
/// adjusts the amount on the list. Re-adding the same deal is therefore idempotent (a no-op top-up of the
/// existing Deal contribution), satisfying the no-duplicate requirement (DJ5).</para>
///
/// <para>Lives in <c>Plantry.Web</c> (the composition root referencing both Deals and Shopping) so the Deals
/// projects keep their <c>→ SharedKernel only</c> dependency.</para>
/// </summary>
public sealed class DealShoppingListWriterAdapter(
    IShoppingListRepository repository,
    IShoppingCatalogReader catalogReader,
    IClock clock,
    ITenantContext tenant) : IDealShoppingListWriter
{
    public async Task AddItemAsync(Guid productId, DealId dealId, CancellationToken ct = default)
    {
        var command = new AddItemCommand(
            productId: productId,
            freeText: null,
            quantity: 1m,
            unitId: null,
            note: null,
            source: ItemSource.Deal,
            sourceRef: dealId.Value,
            intentionalDuplicate: false,
            repository: repository,
            catalogReader: catalogReader,
            clock: clock,
            tenant: tenant);

        var result = await command.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"DealShoppingListWriterAdapter.AddItemAsync failed for product {productId}: " +
                $"{result.Error.Code} — {result.Error.Description}");
    }
}
