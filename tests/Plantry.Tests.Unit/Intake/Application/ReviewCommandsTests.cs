using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for the review-form application surface: the per-line resolution
/// commands (<see cref="ResolveLineCommand"/>, <see cref="ConfirmLineAsNewCommand"/>,
/// <see cref="DismissLineCommand"/>), the whole-session <see cref="DiscardSessionCommand"/>, and the
/// <see cref="GetSessionForReviewQuery"/> read. Each covers the happy path plus the tenant, not-found, and
/// invalid line/session status guards — the same error shapes ParseSession/CommitSession use.
/// </summary>
public sealed class ReviewCommandsTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _categoryId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    /// <summary>A Ready session carrying a single Pending line (the post-parse state edits run against).</summary>
    private (ImportSession Session, ImportLine Line) ReadySessionWithLine()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, """{"x":1}""");
        session.MarkReady("Superstore", Clock.UtcNow);
        return (session, line);
    }

    private static FakeImportSessionRepository RepoWith(ImportSession session)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return repo;
    }

    // ── ResolveLineCommand ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_Confirms_The_Line_Against_An_Existing_Product()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ResolveLineCommand(
            session.Id, line.Id, _productId, skuId: null, 2m, _unitId, _locationId,
            expiryDate: new DateOnly(2026, 12, 1), price: 4.99m, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Confirmed, line.Status);
        Assert.Equal(_productId, line.ProductId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.99m, line.Price);
        Assert.False(line.IsNewProduct);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Resolve_Fails_When_No_Household_In_Context()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ResolveLineCommand(
            session.Id, line.Id, _productId, null, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Resolve_Fails_When_Session_Not_Found()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = new FakeImportSessionRepository(); // not added

        var cmd = new ResolveLineCommand(
            session.Id, line.Id, _productId, null, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Resolve_Fails_When_Line_Not_Found()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ResolveLineCommand(
            session.Id, ImportLineId.New(), _productId, null, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Resolve_Fails_When_Session_Not_Ready()
    {
        var (session, line) = ReadySessionWithLine();
        session.Discard(); // session no longer Ready
        var repo = RepoWith(session);

        var cmd = new ResolveLineCommand(
            session.Id, line.Id, _productId, null, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Resolve_Surfaces_The_Domain_Guard_For_A_Dismissed_Line()
    {
        var (session, line) = ReadySessionWithLine();
        line.Dismiss();
        var repo = RepoWith(session);

        var cmd = new ResolveLineCommand(
            session.Id, line.Id, _productId, null, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyDismissed", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── ConfirmLineAsNewCommand ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmAsNew_Records_New_Product_Intent_Without_A_ProductId()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ConfirmLineAsNewCommand(
            session.Id, line.Id, "Artisan sourdough", _categoryId, 1m, _unitId, _locationId,
            expiryDate: null, price: 6.00m, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Confirmed, line.Status);
        Assert.True(line.IsNewProduct);
        Assert.Null(line.ProductId);
        Assert.Equal("Artisan sourdough", line.NewProductName);
        Assert.Equal(_categoryId, line.NewProductCategoryId);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmAsNew_Fails_When_No_Household_In_Context()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ConfirmLineAsNewCommand(
            session.Id, line.Id, "X", _categoryId, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmAsNew_Fails_When_Session_Not_Ready()
    {
        var (session, line) = ReadySessionWithLine();
        session.Discard();
        var repo = RepoWith(session);

        var cmd = new ConfirmLineAsNewCommand(
            session.Id, line.Id, "X", _categoryId, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmAsNew_Surfaces_The_Domain_Guard_For_A_Blank_Name()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new ConfirmLineAsNewCommand(
            session.Id, line.Id, "   ", _categoryId, 1m, _unitId, _locationId, null, null,
            repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.MissingProductName", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── DismissLineCommand ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_Marks_The_Line_Dismissed()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new DismissLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Dismissed, line.Status);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Dismiss_Fails_When_No_Household_In_Context()
    {
        var (session, line) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new DismissLineCommand(session.Id, line.Id, repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Dismiss_Fails_When_Session_Not_Ready()
    {
        var (session, line) = ReadySessionWithLine();
        session.Discard();
        var repo = RepoWith(session);

        var cmd = new DismissLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
    }

    [Fact]
    public async Task Dismiss_Fails_When_Line_Not_Found()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new DismissLineCommand(session.Id, ImportLineId.New(), repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    // ── RestoreLineCommand ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_Returns_A_Dismissed_Line_To_Pending()
    {
        var (session, line) = ReadySessionWithLine();
        line.Dismiss();
        var repo = RepoWith(session);

        var cmd = new RestoreLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Restore_Fails_When_No_Household_In_Context()
    {
        var (session, line) = ReadySessionWithLine();
        line.Dismiss();
        var repo = RepoWith(session);

        var cmd = new RestoreLineCommand(session.Id, line.Id, repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Restore_Fails_When_Session_Not_Ready()
    {
        var (session, line) = ReadySessionWithLine();
        line.Dismiss();
        session.Discard();
        var repo = RepoWith(session);

        var cmd = new RestoreLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Restore_Surfaces_The_Domain_Guard_For_A_Non_Dismissed_Line()
    {
        var (session, line) = ReadySessionWithLine(); // Pending, never dismissed
        var repo = RepoWith(session);

        var cmd = new RestoreLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotDismissed", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── DiscardSessionCommand ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discard_Marks_The_Session_Discarded()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new DiscardSessionCommand(session.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportStatus.Discarded, session.Status);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Discard_Fails_When_No_Household_In_Context()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var cmd = new DiscardSessionCommand(session.Id, repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Discard_Fails_When_Session_Not_Found()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = new FakeImportSessionRepository(); // not added

        var cmd = new DiscardSessionCommand(session.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Discard_Surfaces_The_Domain_Guard_For_A_Committed_Session()
    {
        var (session, line) = ReadySessionWithLine();
        // Drive the session to Committed so Discard's domain invariant fires.
        line.Confirm(_productId, null, 1m, _unitId, _locationId, null, 1m);
        line.MarkCommitted(Guid.CreateVersion7(), null);
        session.MarkCommitted(Clock.UtcNow);
        var repo = RepoWith(session);

        var cmd = new DiscardSessionCommand(session.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── GetSessionForReviewQuery ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_Loads_Session_Lines_And_Reference_Data()
    {
        var (session, line) = ReadySessionWithLine();
        line.Confirm(_productId, null, 2m, _unitId, _locationId, null, 4.99m);
        var repo = RepoWith(session);

        var reference = new ReviewReferenceData(
            [new ReviewProductOption(_productId, "Flour", "kg", DefaultLocationId: null, Skus: [])],
            [new ReviewUnitOption(_unitId, "kg", "Kilogram")],
            [new ReviewLocationOption(_locationId, "Pantry")],
            [new ReviewCategoryOption(_categoryId, "Baking")]);
        var refProvider = new FakeReviewReferenceDataProvider(reference);

        var query = new GetSessionForReviewQuery(
            session.Id, repo, refProvider, new FakeTenantContext(_household));
        var result = await query.ExecuteAsync();

        Assert.True(result.IsSuccess);
        var view = result.Value;
        Assert.Equal(session.Id.Value, view.SessionId);
        Assert.Equal(ImportStatus.Ready, view.Status);
        Assert.Equal("Superstore", view.MerchantText);
        var lineView = Assert.Single(view.Lines);
        Assert.Equal(line.Id.Value, lineView.LineId);
        Assert.Equal(LineStatus.Confirmed, lineView.Status);
        Assert.Equal(_productId, lineView.ProductId);
        Assert.Equal(4.99m, lineView.Price);
        Assert.Same(reference, view.ReferenceData);
        Assert.Equal(1, refProvider.Calls);
    }

    [Fact]
    public async Task Review_Orders_Lines_By_LineNo()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(3, "Milk", SuggestedConfidence.Low, null);
        session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        session.AddLine(2, "Eggs", SuggestedConfidence.None, null);
        session.MarkReady(null, Clock.UtcNow);
        var repo = RepoWith(session);

        var query = new GetSessionForReviewQuery(
            session.Id, repo, new FakeReviewReferenceDataProvider(), new FakeTenantContext(_household));
        var result = await query.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 3], result.Value.Lines.Select(l => l.LineNo));
    }

    [Fact]
    public async Task Review_Fails_When_No_Household_In_Context()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = RepoWith(session);

        var query = new GetSessionForReviewQuery(
            session.Id, repo, new FakeReviewReferenceDataProvider(), new FakeTenantContext(null));
        var result = await query.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Review_Fails_When_Session_Not_Found()
    {
        var (session, _) = ReadySessionWithLine();
        var repo = new FakeImportSessionRepository(); // not added

        var query = new GetSessionForReviewQuery(
            session.Id, repo, new FakeReviewReferenceDataProvider(), new FakeTenantContext(_household));
        var result = await query.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
