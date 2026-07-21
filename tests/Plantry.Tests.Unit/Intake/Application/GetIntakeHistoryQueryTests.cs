using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository) for <see cref="GetIntakeHistoryQuery"/> — the receipt-intake-history.md
/// H5/H6 row projection: every status but Parsing appears, item count/total follow the shared H6 rule,
/// and paging carries a cursor only when the page was full.
/// </summary>
public sealed class GetIntakeHistoryQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private ImportSession NewSession() =>
        ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);

    private static FakeImportSessionRepository RepoWith(params ImportSession[] sessions)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.AddRange(sessions);
        return repo;
    }

    [Fact]
    public async Task Includes_every_status_but_parsing()
    {
        var committed = NewSession();
        var committedLine = committed.AddLine(1, "Milk", SuggestedConfidence.High, null, suggestedPrice: 3m);
        committed.MarkReady("Store A", Clock.UtcNow);
        committedLine.Confirm(_productId, null, 1m, _unitId, _locationId, null, 3m);
        committedLine.MarkCommitted(Guid.NewGuid(), null);
        committed.MarkCommitted(Clock.UtcNow);

        var ready = NewSession();
        ready.AddLine(1, "Bread", SuggestedConfidence.High, null);
        ready.MarkReady("Store B", Clock.UtcNow);

        var failed = NewSession();
        failed.MarkParsingFailed("bad image");

        var discarded = NewSession();
        discarded.MarkReady("Store C", Clock.UtcNow);
        discarded.Discard();

        var parsing = NewSession(); // still Parsing — excluded

        var repo = RepoWith(committed, ready, failed, discarded, parsing);
        var page = await new GetIntakeHistoryQuery(repo).ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(4, page.Rows.Count);
        Assert.DoesNotContain(page.Rows, r => r.Id == parsing.Id);
        Assert.Contains(page.Rows, r => r.Id == committed.Id && r.Status == ImportStatus.Committed);
        Assert.Contains(page.Rows, r => r.Id == ready.Id && r.Status == ImportStatus.Ready);
        Assert.Contains(page.Rows, r => r.Id == failed.Id && r.Status == ImportStatus.Failed);
        Assert.Contains(page.Rows, r => r.Id == discarded.Id && r.Status == ImportStatus.Discarded);
    }

    [Fact]
    public async Task Committed_row_prefers_session_total_over_line_price_sum()
    {
        var session = NewSession();
        var line = session.AddLine(1, "Milk", SuggestedConfidence.High, null);
        session.MarkReady("Store", Clock.UtcNow, new ReceiptMetadata(Total: 42.00m));
        line.Confirm(_productId, null, 1m, _unitId, _locationId, null, price: 3.99m);
        line.MarkCommitted(Guid.NewGuid(), null);
        session.MarkCommitted(Clock.UtcNow);

        var page = await new GetIntakeHistoryQuery(RepoWith(session)).ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(42.00m, Assert.Single(page.Rows).Total);
    }

    [Fact]
    public async Task Committed_row_falls_back_to_committed_line_price_sum_when_no_receipt_total()
    {
        var session = NewSession();
        var line1 = session.AddLine(1, "Milk", SuggestedConfidence.High, null);
        var line2 = session.AddLine(2, "Bread", SuggestedConfidence.High, null);
        session.MarkReady("Store", Clock.UtcNow); // no Total
        line1.Confirm(_productId, null, 1m, _unitId, _locationId, null, price: 3.99m);
        line2.Confirm(_productId, null, 1m, _unitId, _locationId, null, price: 2.50m);
        line1.MarkCommitted(Guid.NewGuid(), null);
        line2.MarkCommitted(Guid.NewGuid(), null);
        session.MarkCommitted(Clock.UtcNow);

        var page = await new GetIntakeHistoryQuery(RepoWith(session)).ExecuteAsync(HouseholdId.From(_householdId));

        Assert.Equal(3.99m + 2.50m, Assert.Single(page.Rows).Total);
    }

    [Fact]
    public async Task Ready_row_sums_suggested_prices_and_counts_all_parsed_lines()
    {
        var session = NewSession();
        session.AddLine(1, "Milk", SuggestedConfidence.High, null, suggestedPrice: 3.99m);
        session.AddLine(2, "Bread", SuggestedConfidence.High, null, suggestedPrice: 2.50m);
        session.AddLine(3, "Loyalty card scan", SuggestedConfidence.None, null); // no suggested price
        session.MarkReady("Store", Clock.UtcNow);

        var row = Assert.Single((await new GetIntakeHistoryQuery(RepoWith(session)).ExecuteAsync(HouseholdId.From(_householdId))).Rows);

        Assert.Equal(3, row.ItemCount); // every parsed line, not just priced ones
        Assert.Equal(3.99m + 2.50m, row.Total);
    }

    [Fact]
    public async Task Failed_and_discarded_rows_have_no_item_count_or_total()
    {
        var failed = NewSession();
        failed.MarkParsingFailed("bad image");

        var row = Assert.Single((await new GetIntakeHistoryQuery(RepoWith(failed)).ExecuteAsync(HouseholdId.From(_householdId))).Rows);

        Assert.Null(row.ItemCount);
        Assert.Null(row.Total);
    }

    [Fact]
    public async Task Date_prefers_purchase_date_over_created_at()
    {
        var session = NewSession();
        session.MarkReady("Store", Clock.UtcNow, new ReceiptMetadata(PurchaseDate: new DateOnly(2026, 3, 14)));

        var row = Assert.Single((await new GetIntakeHistoryQuery(RepoWith(session)).ExecuteAsync(HouseholdId.From(_householdId))).Rows);

        Assert.Equal(new DateOnly(2026, 3, 14), row.Date);
    }

    [Fact]
    public async Task NextCursor_is_null_when_the_page_is_shorter_than_the_requested_size()
    {
        var session = NewSession();
        session.MarkParsingFailed("x");

        var page = await new GetIntakeHistoryQuery(RepoWith(session))
            .ExecuteAsync(HouseholdId.From(_householdId), take: 10);

        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task NextCursor_is_set_when_the_page_exactly_fills_the_requested_size()
    {
        var s1 = NewSession();
        s1.MarkParsingFailed("x");
        var s2 = NewSession();
        s2.MarkParsingFailed("x");

        var page = await new GetIntakeHistoryQuery(RepoWith(s1, s2))
            .ExecuteAsync(HouseholdId.From(_householdId), take: 2);

        Assert.NotNull(page.NextCursor);
        Assert.Equal(2, page.Rows.Count);
    }
}
