using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Intake;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the receipt-upload abuse gate. Boots the real <c>Plantry.Web</c> pipeline
/// (routing, authorization, <c>RlsMiddleware</c>, the <c>[RequestSizeLimit]</c>/<c>[RequestFormLimits]</c>
/// page attributes, and the <see cref="ReceiptUploadRateLimiter"/>) but swaps the Postgres-backed import
/// session repository for the in-memory fake so the page renders and the write path is inert — no database
/// is touched. Auth is the header-driven test scheme.
///
/// <para>The rate-limit burst can be set low per instance so a rate-limit assertion needs only a handful of
/// requests; because the limiter is a singleton, rate-limit tests should use a <em>fresh</em> factory to
/// isolate its counters (mirroring the review boundary tests' local-factory pattern).</para>
/// </summary>
public sealed class UploadFragmentFactory : WebApplicationFactory<Program>
{
    private readonly int _burstPermitLimit;

    /// <summary>Parameterless ctor for use as an xUnit class fixture (generous default burst). xUnit class
    /// fixtures require exactly one public constructor, so the burst-limited variant is a static factory.</summary>
    public UploadFragmentFactory() : this(1000) { }

    private UploadFragmentFactory(int burstPermitLimit) => _burstPermitLimit = burstPermitLimit;

    /// <summary>A factory whose burst limit is low enough that a rate-limit test can trip it in a few requests.</summary>
    public static UploadFragmentFactory WithBurstLimit(int burstPermitLimit) => new(burstPermitLimit);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Household display currency (plantry-2x6e.2): the "This month" total reads IDisplayCurrency now.
            services.AddFakeDisplayCurrency();
            services.AddFakeExpiringSoonHorizon();

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(ReviewSessionFixture.SnapshotDate));

            // Fake the session repository so the GET (recent-intakes list) renders without a DB and the
            // upload write path (AddAsync/SaveChanges) is a no-op. FindAsync returns null for the staged id,
            // so with the no-key DisabledReceiptParser an accepted upload lands on the soft-fail fragment
            // (HTTP 200) — enough to prove it cleared the abuse gates without needing a real parse/DB.
            services.RemoveAll<IImportSessionRepository>();
            services.AddScoped<IImportSessionRepository>(sp =>
                new FakeImportSessionRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    ReviewSessionFixture.Build(ReviewSessionFixture.HouseholdAId)));

            // The catalog hint provider queries the Catalog DB; with no database in this harness an admitted
            // upload would 500 while fetching hints. Fake it so the parse path (soft-failing on the no-key
            // parser) stays DB-free and an accepted upload can render its fragment.
            services.RemoveAll<ICatalogHintProvider>();
            services.AddScoped<ICatalogHintProvider, FakeCatalogHintProvider>();

            // The Upload GET now composes the "This month" card, which calls the Inventory count queries.
            // Those hit InventoryDbContext/Catalog (Postgres) — absent in this DB-free harness — so swap in a
            // stub that returns fixed counts. Zero here so the page renders the empty-month/no-stock state;
            // the counts themselves are asserted at the model layer (UploadModelStatsTests).
            services.RemoveAll<InventoryQueryService>();
            services.AddScoped<InventoryQueryService>(_ => new StubInventoryQueryService(inStock: 0, expiringSoon: 0));

            // Tunable limits: keep the burst tight enough for the rate-limit test, daily effectively unbounded.
            services.Configure<ReceiptUploadRateLimitOptions>(o =>
            {
                o.BurstPermitLimit = _burstPermitLimit;
                o.BurstWindowSeconds = 60;
                o.DailyPermitLimit = 1_000_000;
                o.DailyWindowHours = 24;
            });
        });
    }
}

/// <summary>No-op catalog hint provider — an admitted upload's parse path resolves hints without a DB.</summary>
internal sealed class FakeCatalogHintProvider : ICatalogHintProvider
{
    public Task<IReadOnlyList<ProductHint>> GetHintsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductHint>>([]);
}
