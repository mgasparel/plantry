using Plantry.Deals.Application;
using Plantry.Inventory.Application;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for the Deals <see cref="IPurchaseFrequencyReader"/> — supplies the stock-up alert read
/// model (P5-10 / DJ5) with per-product purchase counts by delegating to <b>Inventory</b>'s
/// <see cref="IPurchaseJournalReader"/>, which reads Inventory's own purchase journal
/// (<c>StockReason.Purchase</c> movements, DL-O4) grouped in the database.
///
/// <para>Lives in <c>Plantry.Web</c> (the composition root that already references both Deals and Inventory)
/// so <c>Plantry.Deals</c> stays free of any Inventory dependency (ADR-010/DM-3), mirroring
/// <see cref="DealCatalogProductReaderAdapter"/>. Household scoping is enforced inside Inventory (the
/// InventoryDbContext RLS query filter), so no household argument crosses this boundary.</para>
/// </summary>
public sealed class PurchaseFrequencyReaderAdapter(IPurchaseJournalReader journal) : IPurchaseFrequencyReader
{
    public Task<IReadOnlyDictionary<Guid, int>> PurchaseCountsSinceAsync(
        DateTimeOffset since, CancellationToken ct = default) =>
        journal.CountPurchasesSinceAsync(since, ct);
}
