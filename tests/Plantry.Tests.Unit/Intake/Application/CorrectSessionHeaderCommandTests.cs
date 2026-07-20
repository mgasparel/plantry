using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1/L2 tests for <see cref="CorrectSessionHeaderCommand"/> (plantry-yobz) — the review-time header
/// correction over fake ports: gating to Ready, tenancy, the picked-store validation against the
/// household's active stores, and that the domain fields are persisted.
/// </summary>
public sealed class CorrectSessionHeaderCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _storeId = Guid.CreateVersion7();

    private ImportSession ReadySession(string? merchant = "Store #100616")
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        session.MarkReady(merchant, Clock.UtcNow);
        return session;
    }

    private FakeReviewReferenceDataProvider ReferenceWithStore() =>
        new(new ReviewReferenceData([], [], [], [], [new ReviewStoreOption(_storeId, "Food Basics")]));

    private CorrectSessionHeaderCommand Command(
        ImportSession session, FakeImportSessionRepository repo,
        string? merchantText = "Food Basics", Guid? selectedStoreId = null,
        DateOnly? purchaseDate = null, TimeOnly? purchaseTime = null,
        FakeReviewReferenceDataProvider? reference = null,
        Guid? household = null) =>
        new(session.Id, merchantText, selectedStoreId, purchaseDate, purchaseTime,
            repo, reference ?? ReferenceWithStore(), Clock,
            new FakeTenantContext(household ?? _household), NullLogger<CorrectSessionHeaderCommand>.Instance);

    [Fact]
    public async Task Corrects_The_Header_On_A_Ready_Session()
    {
        var session = ReadySession();
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Command(session, repo,
            merchantText: "Food Basics", selectedStoreId: _storeId,
            purchaseDate: new DateOnly(2026, 7, 19), purchaseTime: new TimeOnly(17, 5)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Food Basics", session.MerchantText);
        Assert.Equal(_storeId, session.SelectedStoreId);
        Assert.Equal(new DateOnly(2026, 7, 19), session.PurchaseDate);
        Assert.Equal(new TimeOnly(17, 5), session.PurchaseTime);
    }

    [Fact]
    public async Task Accepts_A_Null_Store_Id_As_The_Merchant_Text_Path()
    {
        var session = ReadySession();
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Command(session, repo,
            merchantText: "Corner Store", selectedStoreId: null).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Corner Store", session.MerchantText);
        Assert.Null(session.SelectedStoreId);
    }

    [Fact]
    public async Task Rejects_A_Store_Id_Not_Among_The_Households_Active_Stores()
    {
        var session = ReadySession();
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Command(session, repo,
            merchantText: "Ghost Store", selectedStoreId: Guid.CreateVersion7()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.UnknownStore", result.Error.Code);
        Assert.Null(session.SelectedStoreId); // header untouched on rejection
    }

    [Fact]
    public async Task Fails_When_Session_Is_Not_Ready()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session); // still Parsing

        var result = await Command(session, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var session = ReadySession();
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var nullTenantCmd = new CorrectSessionHeaderCommand(
            session.Id, "Food Basics", null, null, null, repo, ReferenceWithStore(), Clock,
            new FakeTenantContext(null), NullLogger<CorrectSessionHeaderCommand>.Instance);
        var result = await nullTenantCmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_Session_Not_Found()
    {
        var session = ReadySession();
        var repo = new FakeImportSessionRepository(); // session not added

        var result = await Command(session, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
