using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Market.Application;
using Plantry.Market.Domain;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Pantry;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// L4 WebApplicationFactory for the intake review form. It boots the real <c>Plantry.Web</c> pipeline
/// (routing, authorization, the production <see cref="RlsMiddleware"/> and Razor rendering) but replaces the
/// three Postgres-backed seams the review handlers depend on — the import-session repository and the review
/// reference-data provider — with in-memory fakes, and swaps cookie auth for a header-driven test scheme. No
/// database is touched, so the rendered htmx fragments are deterministic.
///
/// <para>A fixed-date <see cref="IClock"/> is also registered so that the product-default expiry prefill
/// (today + DefaultDueDays) emitted into Alpine x-data is stable across test runs — without pinning the
/// clock, the rendered HTML would differ each calendar day and defeat the snapshot baselines.</para>
/// </summary>
public sealed class ReviewFragmentFactory : WebApplicationFactory<Program>
{
    /// <summary>The fixed Ready session that household A owns; its id is the one to route to.</summary>
    public ImportSession SessionA { get; } = ReviewSessionFixture.Build(ReviewSessionFixture.HouseholdAId);

    public Guid SessionAId => SessionA.Id.Value;

    /// <summary>
    /// Fake price-history seam for the review page's trip-context stats (plantry-bb7p): the page now
    /// injects <see cref="IPriceObservationRepository"/>/<see cref="IUnitPriceCalculator"/> directly (same
    /// pattern as the Pantry product-detail page), so this factory — which deliberately touches no
    /// database — must fake them too, or every GET/SaveLine request in this fixture would try to open a
    /// real Postgres connection. Empty/null by default (no delta ever computed), matching this fixture's
    /// existing "nothing seeded, nothing surprising" baseline; a test that needs a non-null delta overrides
    /// these via <c>factory.WithWebHostBuilder(...)</c> rather than mutating this shared instance, since
    /// xunit reuses one factory across every test in the class.
    /// </summary>
    internal FakePriceObservationRepository PriceObservations { get; } = new();

    /// <inheritdoc cref="PriceObservations"/>
    internal FakeUnitPriceCalculator UnitPriceCalculator { get; } = new(returnValue: null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Non-Development: skips the startup migrations/seeding block and the Dev-pages gate, without paying
        // the cost of a real database.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            // The review page resolves the household display-currency symbol on GET (plantry-2x6e.3) via the real
            // DisplayCurrencyService → IHouseholdRepository (Identity DB). This factory touches no database, so stub
            // the read source with the USD default — the island's money formatters then prefix "$".
            services.AddFakeDisplayCurrency();
            // ── Auth: replace the Identity cookie scheme with the header-driven test handler. Setting it as the
            // default authenticate + challenge scheme means [Authorize] uses it; a request with no household
            // header is unauthenticated and gets challenged (401).
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // ── Clock: replace with a fixed-date clock so the expiry prefill (today + DefaultDueDays) emitted
            // in Alpine x-data is stable across test runs. SnapshotDate is the pinned "today" that the
            // snapshot baselines were generated against.
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new SnapshotFixedClock(ReviewSessionFixture.SnapshotDate));

            // ── Data seams: the review handlers construct GetSessionForReviewQuery / line commands over
            // IImportSessionRepository + IReviewReferenceDataProvider. Fake both so no DbContext query runs.
            // The repository is tenant-scoped via the same scoped ITenantContext the page uses.
            var referenceData = ReviewSessionFixture.ReferenceData();

            services.RemoveAll<IImportSessionRepository>();
            services.AddScoped<IImportSessionRepository>(sp =>
                new FakeImportSessionRepository(sp.GetRequiredService<ITenantContext>(), SessionA));

            services.RemoveAll<IReviewReferenceDataProvider>();
            services.AddScoped<IReviewReferenceDataProvider>(_ =>
                new FakeReviewReferenceDataProvider(referenceData));

            // ── Trip-context price seam (plantry-bb7p) — fake, so this DB-free fixture stays DB-free. ──
            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(PriceObservations);
            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(UnitPriceCalculator);
        });
    }
}

/// <summary>
/// Test clock that always returns a fixed <see cref="DateTimeOffset"/>, making snapshot HTML that
/// embeds today's date (for product-default expiry prefill) stable across calendar days.
/// </summary>
internal sealed class SnapshotFixedClock(DateOnly date) : IClock
{
    public DateTimeOffset UtcNow { get; } = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
