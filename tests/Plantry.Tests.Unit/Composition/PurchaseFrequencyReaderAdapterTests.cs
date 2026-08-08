using Plantry.Pantry.Application;
using Plantry.Web.Deals;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 test for <see cref="PurchaseFrequencyReaderAdapter"/> (plantry-riqy, P5-10/DJ5) — the
/// Deals→Inventory ACL adapter. A thin delegate onto <see cref="IPurchaseJournalReader"/> (household
/// scoping is enforced inside Inventory's RLS query filter, not here), so the only thing worth proving
/// is that it forwards the <c>since</c> instant and the resulting dictionary verbatim.
/// </summary>
public sealed class PurchaseFrequencyReaderAdapterTests
{
    [Fact(DisplayName = "PurchaseCountsSinceAsync forwards the since instant and the result verbatim")]
    public async Task Forwards_Since_And_Result_Verbatim()
    {
        var since = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var productId = Guid.NewGuid();
        var journal = new FakePurchaseJournalReader(since, new Dictionary<Guid, int> { [productId] = 3 });

        var result = await new PurchaseFrequencyReaderAdapter(journal).PurchaseCountsSinceAsync(since);

        Assert.Equal(3, result[productId]);
        Assert.Equal(since, journal.LastRequestedSince);
    }

    [Fact(DisplayName = "PurchaseDatesForProductsAsync forwards the product ids and the result verbatim (plantry-gtgl)")]
    public async Task Forwards_ProductIds_And_Dates_Verbatim()
    {
        var productId = Guid.NewGuid();
        var dates = new List<DateTimeOffset> { new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero) };
        var journal = new FakePurchaseJournalReader(
            default, new Dictionary<Guid, int>(),
            new Dictionary<Guid, IReadOnlyList<DateTimeOffset>> { [productId] = dates });

        var result = await new PurchaseFrequencyReaderAdapter(journal).PurchaseDatesForProductsAsync([productId]);

        Assert.Equal(dates, result[productId]);
        Assert.Equal([productId], journal.LastRequestedProductIds);
    }

    private sealed class FakePurchaseJournalReader(
        DateTimeOffset expectedSince,
        IReadOnlyDictionary<Guid, int> counts,
        IReadOnlyDictionary<Guid, IReadOnlyList<DateTimeOffset>>? dates = null)
        : IPurchaseJournalReader
    {
        public DateTimeOffset? LastRequestedSince { get; private set; }
        public IReadOnlyList<Guid>? LastRequestedProductIds { get; private set; }

        public Task<IReadOnlyDictionary<Guid, int>> CountPurchasesSinceAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            LastRequestedSince = since;
            Assert.Equal(expectedSince, since);
            return Task.FromResult(counts);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DateTimeOffset>>> PurchaseDatesForProductsAsync(
            IEnumerable<Guid> productIds, CancellationToken ct = default)
        {
            LastRequestedProductIds = productIds.ToList();
            return Task.FromResult(dates ?? new Dictionary<Guid, IReadOnlyList<DateTimeOffset>>());
        }
    }
}
