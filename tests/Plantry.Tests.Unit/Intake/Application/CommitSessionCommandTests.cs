using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1/L2 tests for <see cref="CommitSessionCommand"/> over fake ports — the per-line, cross-context
/// commit orchestration (ADR-010): only confirmed lines write, new products are created on the fly, and
/// a mid-batch failure is resumable without double-writing the lines that already committed.
/// </summary>
public sealed class CommitSessionCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private ImportSession ReadySession()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        return session;
    }

    private CommitSessionCommand Commit(
        ImportSession session, FakeImportSessionRepository repo,
        FakeCreateProductPort create, FakeAddStockPort add, FakeRecordPricePort price) =>
        new(session.Id, repo, create, add, price, Clock, new FakeTenantContext(_household),
            NullLogger<CommitSessionCommand>.Instance);

    [Fact]
    public async Task Commits_A_Confirmed_Existing_Product_Line_With_Stock_And_Price()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, """{"x":1}""");
        session.MarkReady("Superstore", Clock.UtcNow);
        var productId = Guid.CreateVersion7();
        line.Confirm(productId, skuId: null, 1m, _unitId, _locationId, expiryDate: null, price: 4.99m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var create = new FakeCreateProductPort();
        var add = new FakeAddStockPort();
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, create, add, price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(create.Calls);                      // existing product, no create
        Assert.Equal(productId, Assert.Single(add.ProductIds));
        Assert.Equal(4.99m, Assert.Single(price.Prices));
        Assert.Equal(LineStatus.Committed, line.Status);
        Assert.NotNull(line.JournalId);
        Assert.NotNull(line.PriceObservationId);
        Assert.Equal(ImportStatus.Committed, session.Status);
    }

    [Fact]
    public async Task Only_Confirmed_Lines_Commit()
    {
        var session = ReadySession();
        var confirmed = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var pending = session.AddLine(2, "Mystery", SuggestedConfidence.Low, null);
        var dismissed = session.AddLine(3, "Loyalty points", SuggestedConfidence.None, null);
        session.MarkReady(null, Clock.UtcNow);
        confirmed.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 2.50m);
        dismissed.Dismiss();

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, new(), add, new()).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(add.ProductIds);                   // only the confirmed line
        Assert.Equal(LineStatus.Committed, confirmed.Status);
        Assert.Equal(LineStatus.Pending, pending.Status);
        Assert.Equal(LineStatus.Dismissed, dismissed.Status);
    }

    [Fact]
    public async Task Creates_A_New_Product_Before_Adding_Stock()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Artisan sourdough", SuggestedConfidence.None, null);
        session.MarkReady(null, Clock.UtcNow);
        var categoryId = Guid.CreateVersion7();
        line.ConfirmAsNew("Artisan sourdough", categoryId, 1m, _unitId, _locationId, null, 6.00m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var create = new FakeCreateProductPort();
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, create, add, new()).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var created = Assert.Single(create.Calls);
        Assert.Equal("Artisan sourdough", created.Name);
        Assert.Equal(categoryId, created.CategoryId);
        // Stock is added against the just-created product, and the line records the new product id.
        Assert.Equal(Assert.Single(add.ProductIds), line.CreatedProductId);
        Assert.NotNull(line.CreatedProductId);
    }

    [Fact]
    public async Task Records_No_Price_When_The_Line_Has_None()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Free sample", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, price: null);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, new(), new(), price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(price.Prices);
        Assert.Null(line.PriceObservationId);
        Assert.Equal(LineStatus.Committed, line.Status);
    }

    [Fact]
    public async Task Mid_Batch_Failure_Is_Resumable_Without_Double_Writing()
    {
        var session = ReadySession();
        var line1 = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var line2 = session.AddLine(2, "Milk", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow);
        var product1 = Guid.CreateVersion7();
        var product2 = Guid.CreateVersion7();
        line1.Confirm(product1, null, 1m, _unitId, _locationId, null, 3m);
        line2.Confirm(product2, null, 2m, _unitId, _locationId, null, 4m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        // First run: the second line's stock write blows up mid-batch.
        var add1 = new FakeAddStockPort { FailOnCall = 2 };
        var firstRun = await Commit(session, repo, new(), add1, new()).ExecuteAsync();

        Assert.True(firstRun.IsFailure);
        Assert.Equal("Intake.CommitFailed", firstRun.Error.Code);
        Assert.Equal(LineStatus.Committed, line1.Status);   // line 1 saved before the failure
        Assert.Equal(LineStatus.Confirmed, line2.Status);   // line 2 never committed
        Assert.Equal(ImportStatus.Ready, session.Status);   // session not marked committed

        // Second run: re-commit resumes — line 1 is skipped, only line 2 is written.
        var add2 = new FakeAddStockPort();
        var secondRun = await Commit(session, repo, new(), add2, new()).ExecuteAsync();

        Assert.True(secondRun.IsSuccess);
        Assert.Equal(product2, Assert.Single(add2.ProductIds)); // line 1 NOT re-added
        Assert.Equal(LineStatus.Committed, line2.Status);
        Assert.Equal(ImportStatus.Committed, session.Status);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var session = ReadySession();
        session.MarkReady(null, Clock.UtcNow);
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var cmd = new CommitSessionCommand(
            session.Id, repo, new FakeCreateProductPort(), new FakeAddStockPort(), new FakeRecordPricePort(),
            Clock, new FakeTenantContext(null), NullLogger<CommitSessionCommand>.Instance);
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_Session_Is_Not_Ready()
    {
        var session = ReadySession(); // still Parsing
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Commit(session, repo, new(), new(), new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_Session_Not_Found()
    {
        var session = ReadySession();
        session.MarkReady(null, Clock.UtcNow);
        var repo = new FakeImportSessionRepository(); // session not added

        var result = await Commit(session, repo, new(), new(), new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
