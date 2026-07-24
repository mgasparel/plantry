using System.Net;
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
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Boundary assertions for the Done screen (/Intake/Done/{id}):
/// <list type="bullet">
///   <item>Unauthenticated requests are challenged.</item>
///   <item>A committed session owned by the requesting household renders the Done page.</item>
///   <item>A Ready (non-committed) session is rejected → redirect to Pantry.</item>
///   <item>A session belonging to another household is rejected → redirect to Pantry.</item>
/// </list>
/// </summary>
public sealed class DoneBoundaryTests : IDisposable
{
    private readonly DonePageFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string DoneUrl(Guid sessionId) => $"/Intake/Done/{sessionId}";

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(DoneUrl(_factory.CommittedSession.Id.Value));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"Expected 401/redirect for an unauthenticated request, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Committed_session_owned_by_household_renders_done_page()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(DoneUrl(_factory.CommittedSession.Id.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Added to your pantry", body);
        Assert.Contains("done-card", body);
        Assert.Contains("stat-grid", body);
        // "View pantry" and "Add another receipt" actions
        Assert.Contains("View pantry", body);
        Assert.Contains("Add another receipt", body);
    }

    [Fact]
    public async Task Done_page_shows_correct_item_count()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(DoneUrl(_factory.CommittedSession.Id.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The fixture commits 2 lines — count should appear on the page.
        Assert.Contains("2", body);
        Assert.Contains("items added", body);
    }

    [Fact(DisplayName = "Done stocked-value renders the € symbol for a EUR household (plantry-2x6e.2)")]
    public async Task Done_stocked_value_uses_household_display_currency()
    {
        using var eurFactory = new DonePageFactory("EUR");
        var client = eurFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(DoneUrl(eurFactory.CommittedSession.Id.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The stocked-value figure (the committed lines' priced total) renders through MoneyDisplay with the
        // household's EUR currency. Read the decoded text (the '€' is emitted HTML-encoded as &#x20AC;) and
        // assert it shows the € symbol and no hardcoded '$' value.
        var text = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(body).Body!.TextContent;
        Assert.Contains("€", text);
        Assert.DoesNotContain("$", text);
    }

    [Fact]
    public async Task Ready_session_redirects_to_pantry()
    {
        // A Ready (non-committed) session must not render the Done screen.
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());

        var response = await client.GetAsync(DoneUrl(_factory.ReadySession.Id.Value));

        // Not committed → redirected away.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("/Pantry", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_belonging_to_other_household_redirects_to_pantry()
    {
        // Household B tries to view household A's committed session.
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdBId.ToString());

        var response = await client.GetAsync(DoneUrl(_factory.CommittedSession.Id.Value));

        // Foreign session → tenant filter returns null → query fails → redirect away.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("/Pantry", location, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// WebApplicationFactory for the Done page. Registers two in-memory sessions:
/// <list type="bullet">
///   <item><see cref="CommittedSession"/> — status Committed, owned by household A.</item>
///   <item><see cref="ReadySession"/> — status Ready, owned by household A (used to assert the guard).</item>
/// </list>
/// </summary>
internal sealed class DonePageFactory(string displayCurrency = "USD") : WebApplicationFactory<Program>
{
    public ImportSession CommittedSession { get; } = BuildCommittedSession();
    public ImportSession ReadySession { get; } = ReviewSessionFixture.Build(ReviewSessionFixture.HouseholdAId);

    private static ImportSession BuildCommittedSession()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var session = ImportSession.Start(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ImportSourceType.Receipt,
            userId: Guid.Empty,
            clock);

        // Two lines: confirm and commit both.
        var line1 = session.AddLine(1, "WHOLE MILK 2L", SuggestedConfidence.High, rawPayload: null,
            suggestedProductId: ReviewSessionFixture.MilkProductId,
            suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var line2 = session.AddLine(2, "FREE RANGE EGGS", SuggestedConfidence.High, rawPayload: null,
            suggestedPrice: 4.50m);

        session.MarkReady("Test Grocer", clock.UtcNow);

        line1.Confirm(ReviewSessionFixture.MilkProductId, skuId: null, quantity: 2m,
            ReviewSessionFixture.LitreUnitId, ReviewSessionFixture.FridgeLocationId,
            expiryDate: new DateOnly(2026, 8, 1), price: 3.99m);

        line2.Confirm(ReviewSessionFixture.EggsProductId, skuId: null, quantity: 12m,
            ReviewSessionFixture.EachUnitId, ReviewSessionFixture.FridgeLocationId,
            expiryDate: new DateOnly(2026, 9, 15), price: 4.50m);

        // Transition to Committed.
        line1.MarkCommitted(journalId: Guid.NewGuid(), priceObservationId: null);
        line2.MarkCommitted(journalId: Guid.NewGuid(), priceObservationId: null);
        session.MarkCommitted(clock.UtcNow);

        return session;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeDisplayCurrency(displayCurrency);
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Register both sessions; the fake repository returns the matching one by id.
            var committedSession = CommittedSession;
            var readySession = ReadySession;

            services.RemoveAll<IImportSessionRepository>();
            services.AddScoped<IImportSessionRepository>(sp =>
                new DualFakeImportSessionRepository(
                    sp.GetRequiredService<ITenantContext>(),
                    committedSession,
                    readySession));

            var referenceData = ReviewSessionFixture.ReferenceData();
            services.RemoveAll<IReviewReferenceDataProvider>();
            services.AddScoped<IReviewReferenceDataProvider>(_ =>
                new FakeReviewReferenceDataProvider(referenceData));
        });
    }
}

/// <summary>
/// In-memory repository that holds two sessions: one committed and one ready.
/// Tenant-scoped: <see cref="FindAsync"/> only returns a session owned by the ambient household.
/// </summary>
internal sealed class DualFakeImportSessionRepository(
    ITenantContext tenant,
    ImportSession committed,
    ImportSession ready) : IImportSessionRepository
{
    private readonly ImportSession[] _sessions = [committed, ready];

    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid)
            return Task.FromResult<ImportSession?>(null);

        var match = _sessions.FirstOrDefault(s =>
            s.Id == sessionId && s.HouseholdId.Value == hid);
        return Task.FromResult(match);
    }

    public Task AddAsync(ImportSession s, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddReceiptAsync(ImportReceipt r, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId id, CancellationToken ct = default) =>
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
