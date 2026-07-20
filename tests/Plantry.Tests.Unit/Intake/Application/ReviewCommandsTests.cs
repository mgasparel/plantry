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

    // ── ReopenLineCommand (plantry-v0wl) ──────────────────────────────────────────────────────────

    /// <summary>A Ready session with a single Confirmed line — the state a reopen (undo) runs against.</summary>
    private (ImportSession Session, ImportLine Line) ReadySessionWithConfirmedLine()
    {
        var (session, line) = ReadySessionWithLine();
        line.Confirm(_productId, skuId: null, 2m, _unitId, _locationId, expiryDate: null, price: 4.99m);
        return (session, line);
    }

    [Fact]
    public async Task Reopen_Returns_A_Confirmed_Line_To_Pending_And_Clears_Resolution()
    {
        var (session, line) = ReadySessionWithConfirmedLine();
        var repo = RepoWith(session);

        var cmd = new ReopenLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);
        Assert.Null(line.ProductId);
        Assert.Null(line.Quantity);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Reopen_Fails_When_No_Household_In_Context()
    {
        var (session, line) = ReadySessionWithConfirmedLine();
        var repo = RepoWith(session);

        var cmd = new ReopenLineCommand(session.Id, line.Id, repo, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Reopen_Fails_When_Session_Not_Found()
    {
        var (session, line) = ReadySessionWithConfirmedLine();
        var repo = new FakeImportSessionRepository(); // not added

        var cmd = new ReopenLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Reopen_Fails_When_Session_Not_Ready()
    {
        var (session, line) = ReadySessionWithConfirmedLine();
        session.Discard(); // session no longer Ready
        var repo = RepoWith(session);

        var cmd = new ReopenLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Reopen_Fails_When_Line_Not_Found()
    {
        var (session, _) = ReadySessionWithConfirmedLine();
        var repo = RepoWith(session);

        var cmd = new ReopenLineCommand(session.Id, ImportLineId.New(), repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Reopen_Surfaces_The_Domain_Guard_For_A_Non_Confirmed_Line()
    {
        var (session, line) = ReadySessionWithLine(); // Pending, never confirmed
        var repo = RepoWith(session);

        var cmd = new ReopenLineCommand(session.Id, line.Id, repo, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    // ── ConfirmLinesCommand (plantry-kr9h) ────────────────────────────────────────────────────────

    /// <summary>Reference data with a single matched product (default unit + location) so a Pending
    /// High-confidence line carrying that product/quantity/unit resolves to a COMPLETE server-side prefill.</summary>
    private FakeReviewReferenceDataProvider MatchedReference() => ReferenceWithProductLocation(_locationId);

    /// <summary>Same, but with the product's default location set explicitly — pass null to model a product
    /// with NO default location, i.e. an INCOMPLETE prefill that must block a bulk confirm.</summary>
    private FakeReviewReferenceDataProvider ReferenceWithProductLocation(Guid? defaultLocationId) =>
        new(new ReviewReferenceData(
            Products: [new ReviewProductOption(_productId, "Flour", "kg", DefaultUnitId: _unitId, DefaultLocationId: defaultLocationId, Skus: [])],
            Units: [new ReviewUnitOption(_unitId, "kg", "Kilogram", ReviewUnitDimension.Mass)],
            Locations: [new ReviewLocationOption(_locationId, "Pantry")],
            Categories: [],
            Stores: []));

    /// <summary>Adds a Pending line whose AI proposal is High-confidence with a full prefill chain
    /// (resolvable product, receipt unit, quantity) — the qualifying input a bulk confirm accepts.</summary>
    private static ImportLine AddQualifyingLine(ImportSession session, int lineNo, string text, Guid productId) =>
        session.AddLine(lineNo, text, SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 2m, suggestedUnitLabel: "kg", suggestedPrice: 4.99m);

    private ConfirmLinesCommand ConfirmLines(
        ImportSession session, IReadOnlyList<ImportLineId> lineIds, FakeImportSessionRepository repo,
        FakeReviewReferenceDataProvider reference, Guid? household) =>
        new(session.Id, lineIds, repo, reference, Clock, new FakeTenantContext(household));

    [Fact]
    public async Task ConfirmLines_Confirms_A_Set_Of_Qualifying_Lines_Atomically_From_Prefill()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var l1 = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        var l2 = AddQualifyingLine(session, 2, "FLOUR 2KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [l1.Id, l2.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { l1.Id.Value, l2.Id.Value }, result.Value);
        foreach (var line in new[] { l1, l2 })
        {
            Assert.Equal(LineStatus.Confirmed, line.Status);
            Assert.Equal(_productId, line.ProductId);   // re-derived from prefill, never the client
            Assert.Equal(2m, line.Quantity);
            Assert.Equal(_unitId, line.UnitId);
            Assert.Equal(_locationId, line.LocationId);
            Assert.Equal(4.99m, line.Price);
        }
        Assert.Equal(1, repo.SaveChangesCalls);         // one save for the whole batch
    }

    [Fact]
    public async Task ConfirmLines_Fails_The_Whole_Command_When_Any_Id_Is_Low_Confidence_And_Confirms_Nothing()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var good = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        var lowConf = session.AddLine(2, "MYSTERY ITEM", SuggestedConfidence.Low, """{"x":1}""",
            suggestedProductId: _productId, suggestedQuantity: 1m, suggestedUnitLabel: "kg");
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [good.Id, lowConf.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmable", result.Error.Code);
        Assert.Contains("MYSTERY ITEM", result.Error.Description);   // error names the offending line
        Assert.Equal(LineStatus.Pending, good.Status);               // atomic — the good line was NOT confirmed
        Assert.Equal(LineStatus.Pending, lowConf.Status);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_A_Qualifying_Line_Has_An_Incomplete_Prefill()
    {
        // High confidence + resolvable product/unit, but the product has no default location and the receipt
        // gave none → the server-side prefill is incomplete (no location), so the line cannot bulk-confirm.
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [line.Id], repo,
            ReferenceWithProductLocation(defaultLocationId: null), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmable", result.Error.Code);
        Assert.Contains("FLOUR 1KG", result.Error.Description);
        Assert.Equal(LineStatus.Pending, line.Status);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_An_Id_Is_Not_Part_Of_The_Session()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var alien = ImportLineId.New(); // id from another session / household
        var result = await ConfirmLines(session, [line.Id, alien], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotInSession", result.Error.Code);
        Assert.Equal(LineStatus.Pending, line.Status);   // nothing confirmed
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_A_Line_Is_Already_Confirmed()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(_productId, skuId: null, 2m, _unitId, _locationId, expiryDate: null, price: 4.99m);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [line.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmable", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_A_Line_Is_Dismissed()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Dismiss();
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [line.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmable", result.Error.Code);
        Assert.Equal(LineStatus.Dismissed, line.Status);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_No_Household_In_Context()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [line.Id], repo, MatchedReference(), household: null).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_Session_Not_Found()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = new FakeImportSessionRepository(); // not added

        var result = await ConfirmLines(session, [line.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_Session_Not_Ready()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var line = AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        session.Discard(); // session no longer Ready
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [line.Id], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ConfirmLines_Fails_When_The_Id_List_Is_Empty()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        AddQualifyingLine(session, 1, "FLOUR 1KG", _productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        var repo = RepoWith(session);

        var result = await ConfirmLines(session, [], repo, MatchedReference(), _household).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.NoLinesToConfirm", result.Error.Code);
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
            [new ReviewProductOption(_productId, "Flour", "kg", DefaultUnitId: _unitId, DefaultLocationId: null, Skus: [])],
            [new ReviewUnitOption(_unitId, "kg", "Kilogram", ReviewUnitDimension.Mass)],
            [new ReviewLocationOption(_locationId, "Pantry")],
            [new ReviewCategoryOption(_categoryId, "Baking")],
            []);
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
