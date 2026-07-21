using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests for <see cref="GetRecentSessionsQuery"/> — the Upload panel's Recent-intakes list. Covers the
/// receipt-intake-history.md H6 amount-rule fix: a committed session must prefer its own resolved truth
/// (receipt total, else committed line prices) rather than the AI-suggested price sum, which may no
/// longer match after the user corrects a price during review.
/// </summary>
public sealed class GetRecentSessionsQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private ImportSession NewSession() =>
        ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);

    private static FakeImportSessionRepository RepoWith(ImportSession session)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return repo;
    }

    [Fact]
    public async Task Committed_session_reports_the_committed_line_price_not_the_ai_suggested_price()
    {
        // The AI suggested $5.00 at parse time; the user corrected it to $3.99 during review. The
        // pre-fix behaviour (always summing SuggestedPrice) would wrongly still report $5.00.
        var session = NewSession();
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null, suggestedPrice: 5.00m);
        session.MarkReady("Store", Clock.UtcNow);
        line.Confirm(_productId, null, 1m, _unitId, _locationId, null, price: 3.99m);
        line.MarkCommitted(Guid.NewGuid(), null);
        session.MarkCommitted(Clock.UtcNow);

        var row = Assert.Single(await new GetRecentSessionsQuery(RepoWith(session))
            .ExecuteAsync(HouseholdId.From(_householdId)));

        Assert.Equal(3.99m, row.Amount);
    }

    [Fact]
    public async Task Ready_session_still_sums_suggested_prices()
    {
        var session = NewSession();
        session.AddLine(1, "Milk", SuggestedConfidence.High, null, suggestedPrice: 3.99m);
        session.AddLine(2, "Bread", SuggestedConfidence.High, null, suggestedPrice: 2.50m);
        session.MarkReady("Store", Clock.UtcNow);

        var row = Assert.Single(await new GetRecentSessionsQuery(RepoWith(session))
            .ExecuteAsync(HouseholdId.From(_householdId)));

        Assert.Equal(3.99m + 2.50m, row.Amount);
    }

    [Fact]
    public async Task Committed_session_with_receipt_total_reports_that_total()
    {
        var session = NewSession();
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null, suggestedPrice: 5.00m);
        session.MarkReady("Store", Clock.UtcNow, new ReceiptMetadata(Total: 42.00m));
        line.Confirm(_productId, null, 1m, _unitId, _locationId, null, price: 3.99m);
        line.MarkCommitted(Guid.NewGuid(), null);
        session.MarkCommitted(Clock.UtcNow);

        var row = Assert.Single(await new GetRecentSessionsQuery(RepoWith(session))
            .ExecuteAsync(HouseholdId.From(_householdId)));

        Assert.Equal(42.00m, row.Amount);
    }
}
