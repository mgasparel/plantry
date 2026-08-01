using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for <c>/Intake/Manual</c> (plantry-45ba.3). Boots the real
/// <c>Plantry.Web</c> pipeline (routing, authorization, <c>RlsMiddleware</c>, Razor rendering,
/// server-side model binding of the posted <c>Input.Lines[n]</c> hidden inputs) but swaps every
/// Postgres-backed seam <see cref="LogManualPurchaseCommand"/> touches for an in-memory fake — no
/// database is touched, so a full single-submit commit (start → confirm lines → CommitSessionCommand)
/// runs end to end and is observable through the fakes' recorded calls.
///
/// <para>Unlike <see cref="ReviewFragmentFactory"/>'s fixed-session <see cref="FakeImportSessionRepository"/>
/// (built for GET-only fragment rendering), <see cref="Plantry.Tests.Web.Infrastructure.ManualPurchaseSessionRepository"/>
/// here is a genuine in-memory store — <c>AddAsync</c> is retained and <c>FindAsync</c> resolves it — because
/// the redirect-to-detail assertion needs the session <see cref="LogManualPurchaseCommand"/> actually started to
/// be resolvable afterwards.</para>
/// </summary>
public sealed class ManualPurchaseFormFactory : WebApplicationFactory<Program>
{
    /// <summary>Household the test auth scheme authenticates every request as. Shared with
    /// <see cref="ReviewSessionFixture"/> so the reference-data ids below line up with existing fixture
    /// constants rather than minting a parallel set.</summary>
    public Guid HouseholdId => ReviewSessionFixture.HouseholdAId;

    /// <summary>Household display currency the page renders money in — set before the first
    /// <see cref="AuthClient"/> call to exercise the currency-symbol thread (precedent:
    /// <c>Deals/DealReviewPageTests</c>' factory knob).</summary>
    public string DisplayCurrency { get; set; } = "USD";

    public ManualPurchaseSessionRepository Sessions { get; } = new();
    public FakeCreateProductPort CreateProduct { get; } = new();
    public FakeAddStockPort AddStock { get; } = new();
    public FakeRecordPricePort RecordPrice { get; } = new();
    public FakeEnsurePurchaseStorePort EnsureStore { get; } = new();
    public FakeSeedConversionPort SeedConversion { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddFakeDisplayCurrency(DisplayCurrency);

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new SnapshotFixedClock(ReviewSessionFixture.SnapshotDate));

            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(Sessions);

            services.RemoveAll<IReviewReferenceDataProvider>();
            services.AddScoped<IReviewReferenceDataProvider>(
                _ => new FakeReviewReferenceDataProvider(ReviewSessionFixture.ReferenceData()));

            services.RemoveAll<ICreateProductPort>();
            services.AddSingleton<ICreateProductPort>(CreateProduct);
            services.RemoveAll<IAddStockPort>();
            services.AddSingleton<IAddStockPort>(AddStock);
            services.RemoveAll<IRecordPricePort>();
            services.AddSingleton<IRecordPricePort>(RecordPrice);
            services.RemoveAll<IEnsurePurchaseStorePort>();
            services.AddSingleton<IEnsurePurchaseStorePort>(EnsureStore);
            services.RemoveAll<ISeedConversionPort>();
            services.AddSingleton<ISeedConversionPort>(SeedConversion);
        });
    }

    public HttpClient AuthClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }
}

/// <summary>
/// A genuine in-memory <see cref="IImportSessionRepository"/> — <c>AddAsync</c>-then-<c>FindAsync</c>
/// round-trips, tenant-scoped like the real repository — so <see cref="ManualPurchaseFormFactory"/> can
/// exercise the full <see cref="LogManualPurchaseCommand"/> → <see cref="CommitSessionCommand"/> path and
/// then resolve the committed session the page redirects to. The remaining query members are unused by
/// the Manual page and stay inert (empty results), mirroring the narrower fake in
/// <c>Plantry.Tests.Unit.Intake.Application.TestDoubles</c> this intentionally parallels — that fake is
/// <c>internal</c> to its own assembly and unreachable from Plantry.Tests.Web.
/// </summary>
public sealed class ManualPurchaseSessionRepository : IImportSessionRepository
{
    private readonly List<ImportSession> _sessions = [];
    public IReadOnlyList<ImportSession> Sessions => _sessions;

    public Task AddAsync(ImportSession session, CancellationToken ct = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult(_sessions.SingleOrDefault(s => s.Id == sessionId));

    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult<ImportReceipt?>(null);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListInMonthWindowAsync(HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<List<ImportSession>> ListHistoryPageAsync(HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());
    public Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(HouseholdId householdId, IReadOnlyCollection<Guid> lineIds, IReadOnlyCollection<Guid> legacyJournalIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportLineProvenanceRow>>([]);
    public Task<ImportLine?> FindLineAsync(HouseholdId householdId, ImportLineId lineId, CancellationToken ct = default) => Task.FromResult<ImportLine?>(null);
    public Task<ImportLine?> FindCommittedLineByJournalIdAsync(HouseholdId householdId, Guid journalId, CancellationToken ct = default) => Task.FromResult<ImportLine?>(null);
}

/// <summary>Records each create and hands back a fresh product id.</summary>
public sealed class FakeCreateProductPort : ICreateProductPort
{
    public List<(string Name, Guid CategoryId, Guid UnitId)> Calls { get; } = [];

    public Task<Guid> CreateAsync(string name, Guid categoryId, Guid defaultUnitId, CancellationToken ct = default)
    {
        Calls.Add((name, categoryId, defaultUnitId));
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Records each stock add and hands back a fresh journal id.</summary>
public sealed class FakeAddStockPort : IAddStockPort
{
    public List<Guid> ProductIds { get; } = [];

    public Task<Guid> AddStockAsync(
        Guid productId, Guid? skuId, decimal quantity, Guid unitId, Guid locationId,
        DateOnly? expiryDate, DateOnly? purchasedAt, Guid userId, Guid? sourceRef = null, CancellationToken ct = default)
    {
        ProductIds.Add(productId);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Records each price write and hands back a fresh observation id.</summary>
public sealed class FakeRecordPricePort : IRecordPricePort
{
    public List<decimal> Prices { get; } = [];

    public Task<Guid> RecordAsync(
        Guid productId, Guid? skuId, decimal price, decimal quantity, Guid unitId,
        string? merchantText, Guid? storeId, Guid sourceRef, DateTimeOffset observedAt, Guid userId, CancellationToken ct = default)
    {
        Prices.Add(price);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

/// <summary>Resolves any merchant name to a stable, per-name store id.</summary>
public sealed class FakeEnsurePurchaseStorePort : IEnsurePurchaseStorePort
{
    private readonly Dictionary<string, Guid> _byName = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    public Task<Guid> EnsureAsync(string merchantName, CancellationToken ct = default)
    {
        Calls.Add(merchantName);
        if (!_byName.TryGetValue(merchantName, out var id))
        {
            id = Guid.CreateVersion7();
            _byName[merchantName] = id;
        }
        return Task.FromResult(id);
    }
}

/// <summary>Records each seeded conversion; unused by the Manual happy-path tests but required to
/// satisfy <see cref="ISeedConversionPort"/>'s DI registration.</summary>
public sealed class FakeSeedConversionPort : ISeedConversionPort
{
    public List<(Guid ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor)> Seeds { get; } = [];

    public Task SeedAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        Seeds.Add((productId, fromUnitId, toUnitId, factor));
        return Task.CompletedTask;
    }
}
