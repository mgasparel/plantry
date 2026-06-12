using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for <see cref="GetCommittedSessionSummaryQuery"/>.
/// Covers: happy path summary projections, the tenant gate, not-found, and the guard that
/// rejects a non-committed (e.g. Ready) session.
/// </summary>
public sealed class GetCommittedSessionSummaryQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _categoryId = Guid.CreateVersion7();

    private ImportSession BuildCommittedSession(Action<ImportSession>? configure = null)
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line1 = session.AddLine(1, "MILK 2L", SuggestedConfidence.High, rawPayload: null,
            suggestedPrice: 3.99m);
        var line2 = session.AddLine(2, "EGGS 12pk", SuggestedConfidence.High, rawPayload: null,
            suggestedPrice: 4.50m);
        session.MarkReady("Test Grocer", Clock.UtcNow);
        configure?.Invoke(session);
        line1.Confirm(_productId, skuId: null, 2m, _unitId, _locationId,
            expiryDate: new DateOnly(2026, 8, 1), price: 3.99m);
        line2.Confirm(_productId, skuId: null, 12m, _unitId, _locationId,
            expiryDate: new DateOnly(2026, 9, 15), price: 4.50m);
        line1.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        line2.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        session.MarkCommitted(Clock.UtcNow);
        return session;
    }

    private static FakeImportSessionRepository RepoWith(ImportSession session)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return repo;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_correct_item_count()
    {
        var session = BuildCommittedSession();
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ItemsAdded);
    }

    [Fact]
    public async Task Returns_sum_of_committed_line_prices()
    {
        var session = BuildCommittedSession();
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3.99m + 4.50m, result.Value.StockedValue);
    }

    [Fact]
    public async Task Returns_soonest_expiry_from_committed_lines()
    {
        var session = BuildCommittedSession();
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Aug 1 is earlier than Sep 15.
        Assert.Equal(new DateOnly(2026, 8, 1), result.Value.SoonestExpiry);
    }

    [Fact]
    public async Task Returns_null_soonest_expiry_when_no_lines_have_expiry()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null, suggestedPrice: 1.00m);
        session.MarkReady("Store", Clock.UtcNow);
        line.Confirm(_productId, skuId: null, 1m, _unitId, _locationId, expiryDate: null, price: 1.00m);
        line.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        session.MarkCommitted(Clock.UtcNow);
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.SoonestExpiry);
    }

    [Fact]
    public async Task Returns_merchant_text_from_session()
    {
        var session = BuildCommittedSession();
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Test Grocer", result.Value.MerchantText);
    }

    [Fact]
    public async Task Category_count_reflects_distinct_new_product_category_ids()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var newLine1 = session.AddLine(1, "ITEM A", SuggestedConfidence.None, rawPayload: null, suggestedPrice: 1m);
        var newLine2 = session.AddLine(2, "ITEM B", SuggestedConfidence.None, rawPayload: null, suggestedPrice: 2m);
        session.MarkReady("Store", Clock.UtcNow);
        newLine1.ConfirmAsNew("Product A", _categoryId, 1m, _unitId, _locationId, null, price: 1m);
        newLine2.ConfirmAsNew("Product B", _categoryId, 1m, _unitId, _locationId, null, price: 2m);
        newLine1.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        newLine2.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        session.MarkCommitted(Clock.UtcNow);
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        // Both lines share the same category id — distinct count is 1.
        Assert.Equal(1, result.Value.CategoryCount);
    }

    [Fact]
    public async Task Stocked_value_is_zero_when_no_prices_present()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null);
        session.MarkReady("Store", Clock.UtcNow);
        line.Confirm(_productId, skuId: null, 1m, _unitId, _locationId, null, price: null);
        line.MarkCommitted(Guid.NewGuid(), priceObservationId: null);
        session.MarkCommitted(Clock.UtcNow);
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.StockedValue);
    }

    // ── Guard: non-committed session ──────────────────────────────────────────

    [Fact]
    public async Task Returns_failure_for_ready_session()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(1, "ITEM", SuggestedConfidence.High, rawPayload: null);
        session.MarkReady("Store", Clock.UtcNow);
        // Session is Ready, not Committed.
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotCommitted", result.Error.Code);
    }

    // ── Guard: not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_not_found_for_unknown_session_id()
    {
        var tenant = new FakeTenantContext(_householdId);

        var result = await new GetCommittedSessionSummaryQuery(
            ImportSessionId.New(), new FakeImportSessionRepository(), tenant).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound.Code, result.Error.Code);
    }

    // ── Guard: unauthenticated ────────────────────────────────────────────────

    [Fact]
    public async Task Returns_unauthorized_when_no_household()
    {
        var session = BuildCommittedSession();
        var tenant = new FakeTenantContext(null);

        var result = await new GetCommittedSessionSummaryQuery(session.Id, RepoWith(session), tenant)
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized.Code, result.Error.Code);
    }
}
