using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Identity.Application;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;

namespace Plantry.Tests.Web.Pantry;

/// <summary>
/// L4 Web integration tests for the "Amend" action + sheet on the Pantry Product Detail History grid
/// (ADR-023 §6/A11, plantry-xjo9): action visibility (intake-sourced vs. manually-added lots, spec
/// acceptance #8), the sheet's receipt-context render + prefill, the blocked/explaining state
/// (closed-by-Correction, A4-iv), the below-consumed guard (A4-ii), and the successful round trip
/// (compensating journal row + line stamp + HX-Redirect toast, mirroring
/// <see cref="ProductDetailMarkOpenedTests"/>'s PRG shape — the only other action on this page that
/// needs a save-toast).
///
/// <para>Reuses the fake seams <see cref="ProductDetailSetPriceTests"/> established for this page
/// (unit/stock/pricing/recipes fakes are assembly-visible within this namespace) and adds a fake
/// <see cref="IImportSessionRepository"/> — the real one needs a live Postgres connection. The real
/// <c>AmendableLineReaderAdapter</c>/<c>AmendStockAdapter</c>/<c>AmendPriceAdapter</c> composition
/// adapters and <c>AmendCommittedLineCommand</c>/<c>GetCommittedLineByJournalIdQuery</c> orchestration
/// run entirely unmodified over the fakes, so this proves the actual cross-context wiring, not a
/// stand-in for it.</para>
/// </summary>
public sealed class ProductDetailAmendTests : IDisposable
{
    private readonly ProductDetailAmendFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private HttpClient AuthClient()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, Guid productId)
    {
        var html = await (await client.GetAsync($"/Pantry/Products/Detail/{productId}"))
            .Content.ReadAsStringAsync();

        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the Detail page.");
        return match.Groups[1].Value;
    }

    [Fact(DisplayName = "Detail GET — the Amend action renders once, only on the intake-sourced Purchase row")]
    public async Task Get_AmendAction_RendersOnlyForIntakeSourcedRow()
    {
        // AuthClient() forces the WAF to build its host (and ConfigureWebHost to run) — _factory.Stock
        // is null until then, so this must come before touching any factory-seeded state.
        var client = AuthClient();

        // A second, manually-added lot on the same product — a Purchase row with no committed
        // ImportLine behind it (spec acceptance #8: non-intake lots offer no amend path).
        _factory.Stock.AddStock(
            1m, ProductDetailAmendFixture.UnitId, ProductDetailAmendFixture.LocationId,
            Guid.NewGuid(), ProductDetailAmendFixture.Clock, sourceType: StockSourceType.Manual);

        var response = await client.GetAsync($"/Pantry/Products/Detail/{ProductDetailAmendFixture.ProductId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, Regex.Matches(html, @"\bPurchase\b").Count); // both Purchase rows render as plain text
        Assert.Single(Regex.Matches(html, "handler=AmendSheet")); // only the intake-sourced one
        Assert.Contains($"entryId={_factory.LotEntryId.Value}", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "AmendSheet GET — renders the receipt context strip and prefills the entered quantity")]
    public async Task AmendSheet_Renders_ReceiptContextAndPrefill()
    {
        var client = AuthClient();

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailAmendFixture.ProductId}?handler=AmendSheet&entryId={_factory.LotEntryId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Fix purchase entry", html, StringComparison.Ordinal);
        Assert.Contains("Farm Boy #12", html, StringComparison.Ordinal);
        Assert.Contains("ONIONS YELLOW", html, StringComparison.Ordinal);
        Assert.Contains("3.98", html, StringComparison.Ordinal);
        Assert.Contains("value=\"1\"", html, StringComparison.Ordinal); // entered-at-review quantity prefilled
    }

    [Fact(DisplayName = "AmendSheet GET — a previously-amended line shows the repeat state and prefills to the prior fix, not the original entry")]
    public async Task AmendSheet_Renders_RepeatAmendmentState_WhenPreviouslyAmended()
    {
        var client = AuthClient();

        // Simulate a prior amendment: entered-at-review was 1 lb (fixture default), now corrected to 2 lb.
        var markResult = _factory.Line.MarkAmended(2m, ProductDetailAmendFixture.Clock.UtcNow);
        Assert.True(markResult.IsSuccess);

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailAmendFixture.ProductId}?handler=AmendSheet&entryId={_factory.LotEntryId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The repeat-state display: original entered quantity is still shown...
        Assert.Contains("Quantity entered at review: <b>1", html, StringComparison.Ordinal);
        // ...alongside the prior fix, with its unit.
        Assert.Contains("previously fixed to <b>2 lb</b>", html, StringComparison.Ordinal);

        // The quantity input (and Alpine's `quantity`/`effective` seed) prefill to the PRIOR FIX (2),
        // not the originally-entered quantity (1) — proves _AmendSheet.cshtml:5's
        // `PreviouslyFixedQuantity ?? EnteredQuantity` fallback picked the amended branch.
        Assert.Contains("value=\"2\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"1\"", html, StringComparison.Ordinal);

        // The Alpine `x-data` seed itself — `effective`/`quantity` — must also pick the prior fix,
        // not just the input's `value=` attribute (a separate, identically-shaped fallback in
        // Detail.cshtml.cs's AmendInput binding). This is what _AmendSheet.cshtml:5's
        // `PreviouslyFixedQuantity ?? EnteredQuantity` actually feeds.
        Assert.Contains("effective: 2", html, StringComparison.Ordinal);
        Assert.Contains("quantity: 2", html, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AmendSheet GET — closed by a later Correction opens the explaining/blocked state, not the form")]
    public async Task AmendSheet_Blocked_WhenClosedByCorrection()
    {
        var client = AuthClient();

        // A Take Stock Correction row dated after the Purchase row closes the amendment window
        // (ADR-023 A4-iv) — product-level, deliberately conservative (A5).
        _factory.Stock.AddStock(
            0.1m, ProductDetailAmendFixture.UnitId, ProductDetailAmendFixture.LocationId,
            Guid.NewGuid(), _factory.LaterClock, reason: StockReason.Correction);

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailAmendFixture.ProductId}?handler=AmendSheet&entryId={_factory.LotEntryId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("alert--warning", html, StringComparison.Ordinal);
        Assert.Contains("amendment window is closed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Save fix", html, StringComparison.Ordinal); // form is not rendered
    }

    [Fact(DisplayName = "AmendSheet GET — a fully-consumed (depleted) lot opens the explaining/blocked state, not the form")]
    public async Task AmendSheet_Blocked_WhenLotDepleted()
    {
        var client = AuthClient();

        // Consume the entire 1-unit lot to zero — resurrecting a depleted lot is out of scope for v1
        // (spec acceptance #8, A4-iii). The block must be evaluated when the sheet opens, not only
        // discovered from ProductStock.AmendPurchase's rejected Inventory.LotNotActive at submit time.
        var converter = await new IdentityConversionProvider().ForProductAsync(ProductDetailAmendFixture.ProductId);
        var consumeResult = _factory.Stock.Consume(
            1m, ProductDetailAmendFixture.UnitId, StockReason.Consumed, converter,
            Guid.NewGuid(), ProductDetailAmendFixture.Clock, targetEntry: _factory.LotEntryId);
        Assert.True(consumeResult.IsSuccess);
        Assert.False(_factory.Stock.Entries.Single(e => e.Id == _factory.LotEntryId).IsActive);

        var response = await client.GetAsync(
            $"/Pantry/Products/Detail/{ProductDetailAmendFixture.ProductId}?handler=AmendSheet&entryId={_factory.LotEntryId.Value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("alert--warning", html, StringComparison.Ordinal);
        Assert.Contains("fully used up", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Save fix", html, StringComparison.Ordinal); // form is not rendered
    }

    [Fact(DisplayName = "Amend — a corrected quantity below what's already consumed is rejected and re-renders the sheet")]
    public async Task Amend_BelowConsumed_ReturnsModelErrorAndReRendersSheet()
    {
        var client = AuthClient();

        // Consume 0.5 of the 1-unit lot, then try to "fix" the receipt quantity down to 0.3 — below
        // the 0.5 already consumed (Inventory.AmendBelowConsumed).
        var converter = await new IdentityConversionProvider().ForProductAsync(ProductDetailAmendFixture.ProductId);
        var consumeResult = _factory.Stock.Consume(
            0.5m, ProductDetailAmendFixture.UnitId, StockReason.Consumed, converter,
            Guid.NewGuid(), ProductDetailAmendFixture.Clock, targetEntry: _factory.LotEntryId);
        Assert.True(consumeResult.IsSuccess);

        var productId = ProductDetailAmendFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=Amend",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmendInput.EntryId", _factory.LotEntryId.Value.ToString()),
                new("AmendInput.Quantity", "0.3"),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("HX-Redirect")); // no redirect — sheet re-rendered in place
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Fix purchase entry", html, StringComparison.Ordinal);
        Assert.Null(_factory.Line.AmendedQuantity); // nothing was actually amended
        Assert.Equal(0, _factory.SessionRepo.SaveChangesCalls); // rejected before the save step
    }

    [Fact(DisplayName = "Amend — happy path appends the compensating journal row, stamps the line, and redirects with the toast")]
    public async Task Amend_HappyPath_AppendsAmendmentRow_StampsLine_RedirectsWithToast()
    {
        var client = AuthClient();
        var productId = ProductDetailAmendFixture.ProductId;
        var token = await GetAntiforgeryTokenAsync(client, productId);

        var response = await client.PostAsync(
            $"/Pantry/Products/Detail/{productId}?handler=Amend",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AmendInput.EntryId", _factory.LotEntryId.Value.ToString()),
                new("AmendInput.Quantity", "3"),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("HX-Redirect"));
        Assert.Equal($"/Pantry/Products/Detail/{productId}", response.Headers.GetValues("HX-Redirect").Single());

        var lot = _factory.Stock.Entries.Single(e => e.Id == _factory.LotEntryId);
        Assert.Equal(3m, lot.Quantity);
        var amendmentRow = _factory.Stock.Journal.Single(j => j.Reason == StockReason.Amendment);
        Assert.Equal(2m, amendmentRow.Delta); // 1 entered -> 3 corrected
        Assert.Equal(3m, _factory.Line.AmendedQuantity);
        // The Purchase row itself is never mutated (ADR-011/A1).
        var purchaseRow = _factory.Stock.Journal.Single(j => j.Reason == StockReason.Purchase);
        Assert.Equal(1m, purchaseRow.Delta);
        Assert.Equal(1, _factory.SessionRepo.SaveChangesCalls);

        // Follow the redirect ourselves (the test client doesn't auto-follow a non-30x header) — the
        // toast rides in the TempData cookie the POST response set, same client instance.
        var follow = await client.GetAsync($"/Pantry/Products/Detail/{productId}");
        // Razor HTML-encodes the em dash as a numeric entity — assert the decoded text instead of the
        // raw character, same convention ProductDetailMoveTests uses for its ❄ glyph.
        var html = System.Net.WebUtility.HtmlDecode(await follow.Content.ReadAsStringAsync());
        Assert.Contains("Purchase entry fixed — now 3", html, StringComparison.Ordinal);
    }
}

// ── Fixture data ──────────────────────────────────────────────────────────────

internal static class ProductDetailAmendFixture
{
    internal static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    internal static readonly HouseholdId Household = Plantry.SharedKernel.HouseholdId.From(HouseholdId);
    internal static readonly IClock Clock = new AmendTestClock(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));

    internal static readonly Guid ProductId = Guid.Parse("11111111-0000-0000-0000-111000000003");
    internal static readonly Guid UnitId = Guid.Parse("22222222-0000-0000-0000-222000000003");
    internal static readonly Guid LocationId = Guid.Parse("33333333-0000-0000-0000-333000000003");

    internal static CatalogUnit BuildUnit() =>
        CatalogUnit.Create(Household, "lb", "Pounds", Dimension.Mass, 1m, isBase: true);
}

internal sealed class AmendTestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}

/// <summary>A single-product <see cref="ICatalogReadFacade"/> keyed off <see cref="ProductDetailAmendFixture.UnitId"/>
/// directly (mirrors <c>FakeMoveCatalogFacade</c>'s same reasoning) — the generic
/// <c>FakeCatalogReadFacade</c> (ProductDetailSetPriceTests) keys off a freshly-minted <c>CatalogUnit.Id</c>
/// instead, which would leave every unit-code lookup here resolving to "?".</summary>
internal sealed class FakeAmendCatalogReadFacade : ICatalogReadFacade
{
    public Task<CatalogProductInfo?> FindProductAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<CatalogProductInfo?>(id == ProductDetailAmendFixture.ProductId
            ? new CatalogProductInfo(
                ProductDetailAmendFixture.ProductId, "Onions, yellow", "Produce",
                ProductDetailAmendFixture.UnitId, "lb", CanHoldStock: true)
            : null);

    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            new Dictionary<Guid, string> { [ProductDetailAmendFixture.UnitId] = "lb" });

    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>
        {
            [ProductDetailAmendFixture.LocationId] = "Pantry",
        });
}

// ── WAF factory ───────────────────────────────────────────────────────────────

internal sealed class ProductDetailAmendFactory : WebApplicationFactory<Program>
{
    internal ProductStock Stock { get; private set; } = null!;
    internal StockEntryId LotEntryId { get; private set; }
    internal ImportLine Line { get; private set; } = null!;
    internal FakeImportSessionRepository SessionRepo { get; private set; } = null!;

    /// <summary>A clock strictly after <see cref="ProductDetailAmendFixture.Clock"/> — used to date a
    /// Correction row after the Purchase row (A4-iv's ordering guard).</summary>
    internal IClock LaterClock { get; } = new AmendTestClock(new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            var unit = ProductDetailAmendFixture.BuildUnit();

            services.RemoveAll<IUnitRepository>();
            services.AddSingleton<IUnitRepository>(new FakeSingleUnitRepository(unit));

            // NOT FakeCatalogReadFacade (ProductDetailSetPriceTests) — that keys GetUnitCodesAsync off
            // the CatalogUnit's own randomly-minted id, but this fixture's lots/journal rows carry
            // ProductDetailAmendFixture.UnitId (a fixed constant, mirroring FakeMoveCatalogFacade's same
            // reasoning) — so unit-code lookups (the toast, the receipt-context strip) resolve for real.
            services.RemoveAll<ICatalogReadFacade>();
            services.AddSingleton<ICatalogReadFacade>(new FakeAmendCatalogReadFacade());

            Stock = ProductStock.Start(
                ProductDetailAmendFixture.Household, ProductDetailAmendFixture.ProductId,
                ProductDetailAmendFixture.Clock);
            var entry = Stock.AddStock(
                1m, ProductDetailAmendFixture.UnitId, ProductDetailAmendFixture.LocationId,
                Guid.NewGuid(), ProductDetailAmendFixture.Clock,
                sourceType: StockSourceType.Intake);
            LotEntryId = entry.Id;

            var stockRepo = new FakeDetailStockRepository();
            stockRepo.Items.Add(Stock);
            services.RemoveAll<IProductStockRepository>();
            services.AddSingleton<IProductStockRepository>(stockRepo);

            services.RemoveAll<IProductConversionProvider>();
            services.AddSingleton<IProductConversionProvider>(new IdentityConversionProvider());

            services.RemoveAll<IStockProvenanceReader>();
            services.AddSingleton<IStockProvenanceReader>(new FakeStockProvenanceReader());

            services.RemoveAll<IPriceObservationRepository>();
            services.AddSingleton<IPriceObservationRepository>(new FakePriceObservationRepository());

            services.RemoveAll<IDisplayCurrency>();
            services.AddSingleton<IDisplayCurrency>(new FakeDisplayCurrency());

            services.RemoveAll<IUnitPriceCalculator>();
            services.AddSingleton<IUnitPriceCalculator>(new FakeUnitPriceCalculator(0.5m));

            services.RemoveAll<Plantry.Recipes.Domain.IRecipeRepository>();
            services.AddSingleton<Plantry.Recipes.Domain.IRecipeRepository>(new FakeRecipeRepository());

            // Intake seam: a fake repository (the real one needs a live Postgres connection) seeded with
            // one committed line whose JournalId matches the lot's own StockEntryId (ADR-023's
            // confusingly-named linkage — see AmendCommittedLineCommand's interpretation note). The real
            // AmendableLineReaderAdapter/AmendCommittedLineCommand/GetCommittedLineByJournalIdQuery run
            // unmodified over it.
            var session = ImportSession.Start(
                ProductDetailAmendFixture.Household, ImportSourceType.Receipt, Guid.NewGuid(),
                ProductDetailAmendFixture.Clock);
            Line = session.AddLine(
                lineNo: 1, receiptText: "ONIONS YELLOW", confidence: SuggestedConfidence.High, rawPayload: null);
            var confirmResult = Line.Confirm(
                ProductDetailAmendFixture.ProductId, skuId: null, quantity: 1m,
                ProductDetailAmendFixture.UnitId, ProductDetailAmendFixture.LocationId,
                expiryDate: null, price: 3.98m);
            if (confirmResult.IsFailure)
                throw new InvalidOperationException(confirmResult.Error.Description);
            var commitResult = Line.MarkCommitted(LotEntryId.Value, priceObservationId: null);
            if (commitResult.IsFailure)
                throw new InvalidOperationException(commitResult.Error.Description);
            session.MarkReady(
                "Farm Boy #12", ProductDetailAmendFixture.Clock.UtcNow,
                new ReceiptMetadata(PurchaseDate: DateOnly.FromDateTime(ProductDetailAmendFixture.Clock.UtcNow.LocalDateTime)));

            SessionRepo = new FakeImportSessionRepository();
            SessionRepo.Sessions.Add(session);
            services.RemoveAll<IImportSessionRepository>();
            services.AddSingleton<IImportSessionRepository>(SessionRepo);
        });
    }
}

/// <summary>
/// In-memory <see cref="IImportSessionRepository"/> stand-in — the real EF-backed repository needs a
/// live Postgres connection. Implements only what the Amend path actually calls
/// (<see cref="FindCommittedLineByJournalIdAsync"/>, <see cref="FindLineAsync"/>,
/// <see cref="FindAsync"/>, <see cref="SaveChangesAsync"/>); the batch
/// <c>FindCommittedLineIdsByJournalIdsAsync</c> the grid's render pass needs falls through to the
/// interface's own default implementation (loops the single-id method above), so it needs no override
/// here — same default-interface-method precedent <c>IProductStockRepository.ListProductIdsWithStockAsync</c>
/// established.
/// </summary>
internal sealed class FakeImportSessionRepository : IImportSessionRepository
{
    internal List<ImportSession> Sessions { get; } = [];
    internal int SaveChangesCalls { get; private set; }

    public Task AddAsync(ImportSession session, CancellationToken ct = default)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.SingleOrDefault(s => s.Id == sessionId));

    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) =>
        Task.FromResult<ImportReceipt?>(null);

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());

    public Task<bool> HasPendingAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<List<ImportSession>> ListRecentAsync(HouseholdId householdId, int take = 10, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());

    public Task<List<ImportSession>> ListInMonthWindowAsync(
        HouseholdId householdId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());

    public Task<List<ImportSession>> ListHistoryPageAsync(
        HouseholdId householdId, DateTimeOffset? beforeCreatedAt, int take, CancellationToken ct = default) =>
        Task.FromResult(new List<ImportSession>());

    public Task<IReadOnlyList<ImportLineProvenanceRow>> FindLinesForProvenanceAsync(
        HouseholdId householdId, IReadOnlyCollection<Guid> lineIds, IReadOnlyCollection<Guid> legacyJournalIds,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportLineProvenanceRow>>([]);

    public Task<ImportLine?> FindLineAsync(HouseholdId householdId, ImportLineId lineId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.SelectMany(s => s.Lines)
            .SingleOrDefault(l => l.HouseholdId == householdId && l.Id == lineId));

    public Task<ImportLine?> FindCommittedLineByJournalIdAsync(
        HouseholdId householdId, Guid journalId, CancellationToken ct = default) =>
        Task.FromResult(Sessions.SelectMany(s => s.Lines)
            .FirstOrDefault(l => l.HouseholdId == householdId && l.Status == LineStatus.Committed && l.JournalId == journalId));
}
