using Plantry.Inventory.Application;
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

    private sealed class FakePurchaseJournalReader(DateTimeOffset expectedSince, IReadOnlyDictionary<Guid, int> counts)
        : IPurchaseJournalReader
    {
        public DateTimeOffset? LastRequestedSince { get; private set; }

        public Task<IReadOnlyDictionary<Guid, int>> CountPurchasesSinceAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            LastRequestedSince = since;
            Assert.Equal(expectedSince, since);
            return Task.FromResult(counts);
        }
    }
}
