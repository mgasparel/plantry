using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for <see cref="GetCommittedLineByJournalIdQuery"/> — the ADR-023 §6
/// reverse lookup a Pantry Product Detail History row uses to resolve its committed
/// <see cref="ImportLine"/> and decide whether to offer the "Amend" action. Covers the happy path (both
/// an existing-product and a new-product line), the tenant gate, and not-found (a non-intake lot, or a
/// journal id belonging to a different household).
/// </summary>
public sealed class GetCommittedLineByJournalIdQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private static FakeImportSessionRepository RepoWith(ImportSession session)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return repo;
    }

    [Fact]
    public async Task Resolves_A_Committed_Existing_Product_Line_By_Its_JournalId()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "ONIONS YELLOW", SuggestedConfidence.High, null, suggestedPrice: 3.98m);
        session.MarkReady("Superstore", Clock.UtcNow);
        var productId = Guid.CreateVersion7();
        line.Confirm(productId, null, 1m, _unitId, _locationId, null, 3.98m);
        var stockEntryId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, Guid.CreateVersion7());

        var result = await new GetCommittedLineByJournalIdQuery(
            stockEntryId, RepoWith(session), new FakeTenantContext(_householdId)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(line.Id.Value, result.Value.ImportLineId);
        Assert.Equal(session.Id.Value, result.Value.SessionId);
        Assert.Equal(productId, result.Value.ProductId);
        Assert.Equal(1m, result.Value.Quantity);
        Assert.Equal(_unitId, result.Value.UnitId);
        Assert.Null(result.Value.AmendedQuantity);
        Assert.Null(result.Value.AmendedAt);
    }

    [Fact]
    public async Task Resolves_A_New_Product_Lines_Effective_ProductId_From_CreatedProductId()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "Mystery Item", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow);
        line.ConfirmAsNew("Mystery Item", Guid.CreateVersion7(), 1m, _unitId, _locationId, null, 1.99m);
        var stockEntryId = Guid.CreateVersion7();
        var createdProductId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, null, createdProductId);

        var result = await new GetCommittedLineByJournalIdQuery(
            stockEntryId, RepoWith(session), new FakeTenantContext(_householdId)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(createdProductId, result.Value.ProductId);
    }

    [Fact]
    public async Task Reflects_A_Prior_Amendment()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "ONIONS YELLOW", SuggestedConfidence.High, null, suggestedPrice: 3.98m);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 3.98m);
        var stockEntryId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, Guid.CreateVersion7());
        var amendedAt = Clock.UtcNow;
        line.MarkAmended(3m, amendedAt);

        var result = await new GetCommittedLineByJournalIdQuery(
            stockEntryId, RepoWith(session), new FakeTenantContext(_householdId)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3m, result.Value.AmendedQuantity);
        Assert.Equal(amendedAt, result.Value.AmendedAt);
    }

    [Fact]
    public async Task NotFound_When_No_Committed_Line_Has_That_JournalId()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);

        var result = await new GetCommittedLineByJournalIdQuery(
            Guid.CreateVersion7(), RepoWith(session), new FakeTenantContext(_householdId)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound.Code, result.Error.Code);
    }

    [Fact]
    public async Task NotFound_Across_Households_RLS_Style_Isolation()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        var line = session.AddLine(1, "ONIONS YELLOW", SuggestedConfidence.High, null, suggestedPrice: 3.98m);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 3.98m);
        var stockEntryId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, Guid.CreateVersion7());

        var otherHousehold = Guid.NewGuid();
        var result = await new GetCommittedLineByJournalIdQuery(
            stockEntryId, RepoWith(session), new FakeTenantContext(otherHousehold)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound.Code, result.Error.Code);
    }

    [Fact]
    public async Task Unauthorized_When_No_Household_On_Tenant_Context()
    {
        var session = ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);

        var result = await new GetCommittedLineByJournalIdQuery(
            Guid.CreateVersion7(), RepoWith(session), new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized.Code, result.Error.Code);
    }
}
