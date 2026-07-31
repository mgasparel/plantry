using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Registers <b>empty</b> Deals seams for the Today-page WAF factories (plantry-bpw). The Today
/// <c>IndexModel</c> now consumes the real <see cref="BrowseDeals"/> read service to compute the
/// Phase-5 deal-review banner, so every Today factory must supply the three Deals ports it depends on
/// (<see cref="IDealRepository"/>, <see cref="ICatalogStoreReader"/>, and the Deals-context
/// <see cref="ICatalogProductReader"/>) — otherwise the real Postgres-backed <c>DealRepository</c>
/// would be resolved and hit a database that the L4 harness does not stand up.
/// <para>
/// An empty deal repository makes <c>BrowseDeals</c> report zero pending deals → no deal banner renders,
/// keeping the existing intake/planned-meals fragment tests unaffected. Factories that specifically
/// exercise the deal banner register their own seeded fakes instead of calling this.
/// </para>
/// </summary>
internal static class TodayDealsStubs
{
    public static void RegisterEmpty(IServiceCollection services)
    {
        services.RemoveAll<IDealRepository>();
        services.AddSingleton<IDealRepository>(new FakeDealBrowseRepo());

        services.RemoveAll<ICatalogStoreReader>();
        services.AddSingleton<ICatalogStoreReader>(new FakeDealStoreReader());

        // The Deals-context ICatalogProductReader (distinct from the Recipes-context interface of the
        // same simple name) — BrowseDeals resolves this one specifically.
        services.RemoveAll<ICatalogProductReader>();
        services.AddSingleton<ICatalogProductReader>(new FakeDealProductReader());
    }

    /// <summary>
    /// Registers the Deals seams with a single <b>Pending</b> deal staged into an in-memory repository,
    /// so the real <see cref="BrowseDeals"/> read service partitions it as pending-review. The deal's
    /// validity window is set relative to <b>today</b> (<see cref="SystemClock"/>, the app clock in the
    /// L4 harness): <paramref name="inWindow"/> = true puts <c>valid_to</c> in the future (counts toward
    /// the banner); false puts it in the past so <c>BrowseDeals</c>'s <c>today ≤ valid_to</c> predicate
    /// (DD14) drops it — proving the count is recomputed against the clock, not a stamped value.
    /// </summary>
    public static void RegisterWithPendingDeal(IServiceCollection services, bool inWindow)
    {
        var repo = new FakeDealBrowseRepo();
        repo.Items.Add(BuildPendingDeal(inWindow));

        services.RemoveAll<IDealRepository>();
        services.AddSingleton<IDealRepository>(repo);

        services.RemoveAll<ICatalogStoreReader>();
        services.AddSingleton<ICatalogStoreReader>(new FakeDealStoreReader());

        services.RemoveAll<ICatalogProductReader>();
        services.AddSingleton<ICatalogProductReader>(new FakeDealProductReader());
    }

    private static Deal BuildPendingDeal(bool inWindow)
    {
        var clock = SystemClock.Instance;
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var window = inWindow
            ? ValidityWindow.Create(today.AddDays(-1), today.AddDays(6)).Value   // in-window: today ≤ valid_to
            : ValidityWindow.Create(today.AddDays(-6), today.AddDays(-1)).Value;  // expired: valid_to < today

        var raw = new RawDeal("Fresh Salmon", null, null, 4.99m, null, null, "Save $1", window);
        return Deal.Stage(
            HouseholdId.New(),
            FlyerImportId.New(),
            Guid.NewGuid(),                       // store soft-ref; the banner needs no resolved store name
            raw,
            DealNormalizer.Normalize("Fresh Salmon"),
            MatchProposal.Unmatched(),            // Pending → no committed product
            clock);
    }
}

// ── fakes ─────────────────────────────────────────────────────────────────────
//
// Relocated from Deals/DealsPageTests.cs (plantry-ej84) — the declarations TodayDealsStubs reaches
// back into now live alongside it in Infrastructure/, matching the house convention the other
// Infrastructure/ factories already follow. Still consumed directly by Deals/DealsPageTests.cs and
// Deals/DealReviewPageTests.cs (both already carry `using Plantry.Tests.Web.Infrastructure;`) and by
// Today/TodayIndexModelTests.cs (fully qualified). Pure declaration move: no behaviour change.

public sealed class FakeDealBrowseRepo : IDealRepository
{
    public List<Deal> Items { get; } = [];

    public Task<Deal?> FindAsync(DealId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(d => d.Id == id));

    public Task<List<Deal>> ListBrowsableAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(d => d.Status is DealStatus.Pending or DealStatus.Confirmed).ToList());

    public Task<List<Deal>> ListByFlyerImportAsync(FlyerImportId flyerImportId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(d => d.FlyerImportId == flyerImportId).ToList());

    public Task AddAsync(Deal deal, CancellationToken ct = default) { Items.Add(deal); return Task.CompletedTask; }
    public void Remove(Deal deal) => Items.Remove(deal);
    public void DiscardStagedChanges() { } // no deferred change tracker to reset in this browse fake
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeDealProductReader : ICatalogProductReader
{
    public Dictionary<Guid, DealProductInfo> Items { get; } = new();

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DealProductInfo> result = productIds
            .Where(Items.ContainsKey)
            .ToDictionary(id => id, id => Items[id]);
        return Task.FromResult(result);
    }
}

public sealed class FakeDealStoreReader : ICatalogStoreReader
{
    public Dictionary<Guid, string> Names { get; } = new();

    public Task<CatalogStoreInfo?> FindAsync(Guid storeId, CancellationToken ct = default) =>
        Task.FromResult(Names.TryGetValue(storeId, out var n) ? new CatalogStoreInfo(storeId, n, null) : null);

    public Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = storeIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}
