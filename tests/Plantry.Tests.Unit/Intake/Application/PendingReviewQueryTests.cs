using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 unit tests (fake repository, no DB) for <see cref="PendingReviewQuery"/>.
///
/// Covers:
/// <list type="bullet">
///   <item>Only <c>Ready</c> sessions are returned — Committed, Parsing, Failed, and Discarded are excluded.</item>
///   <item>Results are ordered newest first.</item>
///   <item>Household scoping: only the calling household's sessions appear.</item>
///   <item>ItemCount projection: equals Lines.Count.</item>
///   <item>Amount projection: sum of SuggestedPrice where non-null; null when no lines carry a price.</item>
///   <item>Empty list when no pending sessions exist.</item>
/// </list>
/// </summary>
public sealed class PendingReviewQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly HouseholdId _household = HouseholdId.New();
    private readonly Guid _userId = Guid.CreateVersion7();

    // ── Only Ready sessions are returned ─────────────────────────────────────

    [Fact(DisplayName = "Returns only Ready sessions — Committed and Parsing are excluded")]
    public async Task ReturnsOnlyReadySessions()
    {
        var repo = new FakeImportSessionRepository();

        // Ready — should appear
        var ready = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        ready.AddLine(1, "Milk 2L", SuggestedConfidence.High, null, suggestedPrice: 3.99m);
        ready.MarkReady("Whole Foods", Clock.UtcNow);
        repo.Sessions.Add(ready);

        // Committed — should NOT appear
        var committed = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        committed.AddLine(1, "Eggs", SuggestedConfidence.High, null);
        committed.MarkReady("Trader Joe's", Clock.UtcNow);
        committed.MarkCommitted(Clock.UtcNow);
        repo.Sessions.Add(committed);

        // Parsing — should NOT appear
        var parsing = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        repo.Sessions.Add(parsing);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(ready.Id, result[0].Id);
    }

    [Fact(DisplayName = "Returns only Ready sessions — Failed and Discarded are excluded")]
    public async Task ExcludesFailedAndDiscardedSessions()
    {
        var repo = new FakeImportSessionRepository();

        var failed = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        failed.MarkParsingFailed("oops");
        repo.Sessions.Add(failed);

        var discarded = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        discarded.Discard();
        repo.Sessions.Add(discarded);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Empty(result);
    }

    // ── Household scoping ────────────────────────────────────────────────────

    [Fact(DisplayName = "Household scoping: returns only sessions for the queried household")]
    public async Task HouseholdScoping_ReturnsOnlyOwnSessions()
    {
        var repo = new FakeImportSessionRepository();
        var otherHousehold = HouseholdId.New();

        // This household — should appear
        var ours = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        ours.AddLine(1, "Item", SuggestedConfidence.High, null);
        ours.MarkReady("Our Store", Clock.UtcNow);
        repo.Sessions.Add(ours);

        // Other household — should NOT appear
        var theirs = ImportSession.Start(otherHousehold, ImportSourceType.Receipt, _userId, Clock);
        theirs.AddLine(1, "Item", SuggestedConfidence.High, null);
        theirs.MarkReady("Their Store", Clock.UtcNow);
        repo.Sessions.Add(theirs);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(ours.Id, result[0].Id);
    }

    // ── Empty result ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "Returns empty list when no Ready sessions exist")]
    public async Task ReturnsEmptyList_WhenNoPendingSessions()
    {
        var repo = new FakeImportSessionRepository();

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Empty(result);
    }

    // ── ItemCount projection ──────────────────────────────────────────────────

    [Fact(DisplayName = "ItemCount equals the number of lines on the session")]
    public async Task ItemCount_EqualsLineCount()
    {
        var repo = new FakeImportSessionRepository();

        var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(1, "Milk",  SuggestedConfidence.High, null);
        session.AddLine(2, "Eggs",  SuggestedConfidence.High, null);
        session.AddLine(3, "Flour", SuggestedConfidence.Low,  null);
        session.MarkReady("Costco", Clock.UtcNow);
        repo.Sessions.Add(session);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(3, result[0].ItemCount);
    }

    [Fact(DisplayName = "ItemCount is 0 for a session with no lines")]
    public async Task ItemCount_IsZero_ForSessionWithNoLines()
    {
        var repo = new FakeImportSessionRepository();

        var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        session.MarkReady(null, Clock.UtcNow);
        repo.Sessions.Add(session);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(0, result[0].ItemCount);
    }

    // ── Amount projection ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Amount is sum of SuggestedPrice when all lines carry a price")]
    public async Task Amount_IsSumOfSuggestedPrices()
    {
        var repo = new FakeImportSessionRepository();

        var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(1, "Milk",  SuggestedConfidence.High, null, suggestedPrice: 3.99m);
        session.AddLine(2, "Eggs",  SuggestedConfidence.High, null, suggestedPrice: 4.50m);
        session.MarkReady("Whole Foods", Clock.UtcNow);
        repo.Sessions.Add(session);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(3.99m + 4.50m, result[0].Amount);
    }

    [Fact(DisplayName = "Amount is null when no lines carry a SuggestedPrice")]
    public async Task Amount_IsNull_WhenNoLinesHavePrice()
    {
        var repo = new FakeImportSessionRepository();

        var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(1, "Item A", SuggestedConfidence.High, null, suggestedPrice: null);
        session.AddLine(2, "Item B", SuggestedConfidence.Low,  null, suggestedPrice: null);
        session.MarkReady("No-Price Store", Clock.UtcNow);
        repo.Sessions.Add(session);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Null(result[0].Amount);
    }

    [Fact(DisplayName = "Amount sums only lines with a SuggestedPrice — null lines are skipped")]
    public async Task Amount_SumsOnlyLinesWithPrice_SkipsNulls()
    {
        var repo = new FakeImportSessionRepository();

        var session = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        session.AddLine(1, "Item A", SuggestedConfidence.High, null, suggestedPrice: 5.00m);
        session.AddLine(2, "Item B", SuggestedConfidence.Low,  null, suggestedPrice: null);
        session.MarkReady("Mixed Store", Clock.UtcNow);
        repo.Sessions.Add(session);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Single(result);
        Assert.Equal(5.00m, result[0].Amount);
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Results are ordered newest first (descending CreatedAt)")]
    public async Task ResultsOrderedNewestFirst()
    {
        var repo = new FakeImportSessionRepository();

        // Older session
        var older = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        older.AddLine(1, "Old item", SuggestedConfidence.High, null);
        older.MarkReady("Store A", DateTimeOffset.UtcNow.AddDays(-2));
        repo.Sessions.Add(older);

        // Newer session
        var newer = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        newer.AddLine(1, "New item", SuggestedConfidence.High, null);
        newer.MarkReady("Store B", DateTimeOffset.UtcNow.AddDays(-1));
        repo.Sessions.Add(newer);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Equal(2, result.Count);
        // Newest first — the newer session (more recent CreatedAt) should be first
        Assert.True(result[0].CreatedAt >= result[1].CreatedAt);
    }

    // ── Store name projection ──────────────────────────────────────────────────

    [Fact(DisplayName = "Store maps from MerchantText — null when merchant not detected")]
    public async Task Store_MapsFromMerchantText()
    {
        var repo = new FakeImportSessionRepository();

        var withStore = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        withStore.AddLine(1, "Item", SuggestedConfidence.High, null);
        withStore.MarkReady("Trader Joe's", Clock.UtcNow);
        repo.Sessions.Add(withStore);

        var withoutStore = ImportSession.Start(_household, ImportSourceType.Receipt, _userId, Clock);
        withoutStore.AddLine(1, "Item", SuggestedConfidence.High, null);
        withoutStore.MarkReady(null, Clock.UtcNow);
        repo.Sessions.Add(withoutStore);

        var result = await new PendingReviewQuery(repo).ExecuteAsync(_household);

        Assert.Equal(2, result.Count);
        var storeRow = result.Single(r => r.Store == "Trader Joe's");
        var noStoreRow = result.Single(r => r.Store is null);
        Assert.NotNull(storeRow);
        Assert.Null(noStoreRow.Store);
    }
}
