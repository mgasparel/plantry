using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests (fake repository, no DB) for <see cref="GetSessionForReviewQuery"/> — focusing on the
/// alternative-candidates mapping introduced for the "Did you mean" suggestion block.
/// </summary>
public sealed class GetSessionForReviewQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productA = Guid.CreateVersion7();
    private readonly Guid _productB = Guid.CreateVersion7();
    private readonly Guid _productC = Guid.CreateVersion7();

    private ImportSession BuildReadySession(Action<ImportSession> configure, ReceiptMetadata? metadata = null)
    {
        var session = ImportSession.Start(
            HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);
        configure(session);
        session.MarkReady("Test Grocer", Clock.UtcNow, metadata);
        return session;
    }

    [Fact]
    public async Task View_Carries_Receipt_Metadata_From_The_Session()
    {
        var metadata = new ReceiptMetadata(
            StoreBranch: "42 Market St",
            PurchaseDate: new DateOnly(2026, 6, 7),
            PurchaseTime: new TimeOnly(14, 34),
            Subtotal: 40.00m, Tax: 2.00m, Total: 42.00m,
            PaymentDescriptor: "VISA ****4471 APPROVED",
            ReceiptNumber: "TXN 0472 118");
        var session = BuildReadySession(
            s => s.AddLine(1, "WHOLE MILK 2L", SuggestedConfidence.High, rawPayload: null),
            metadata);

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var view = result.Value;
        Assert.Equal(ImportSourceType.Receipt, view.SourceType);
        Assert.Equal("42 Market St", view.StoreBranch);
        Assert.Equal(new DateOnly(2026, 6, 7), view.PurchaseDate);
        Assert.Equal(new TimeOnly(14, 34), view.PurchaseTime);
        Assert.Equal(40.00m, view.Subtotal);
        Assert.Equal(2.00m, view.Tax);
        Assert.Equal(42.00m, view.Total);
        Assert.Equal("VISA ****4471 APPROVED", view.PaymentDescriptor);
        Assert.Equal("TXN 0472 118", view.ReceiptNumber);
    }

    [Fact]
    public async Task View_Metadata_Is_Null_When_Session_Has_None()
    {
        var session = BuildReadySession(
            s => s.AddLine(1, "WHOLE MILK 2L", SuggestedConfidence.High, rawPayload: null));

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.StoreBranch);
        Assert.Null(result.Value.Total);
        Assert.Null(result.Value.PaymentDescriptor);
    }

    private static FakeImportSessionRepository RepoWith(ImportSession session)
    {
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return repo;
    }

    private GetSessionForReviewQuery Query(ImportSession session) =>
        new(session.Id, RepoWith(session), new FakeReviewReferenceDataProvider(), new FakeTenantContext(_householdId));

    // ── Alternatives mapping ──────────────────────────────────────────────────────

    [Fact]
    public async Task Line_with_two_or_more_alternatives_maps_to_ReviewAlternativeView_list()
    {
        var alts = new[]
        {
            new AlternativeCandidate(_productA, "Cheddar, Mild", 0.88m),
            new AlternativeCandidate(_productB, "Cheddar, Sharp", 0.62m),
        };
        var session = BuildReadySession(s =>
            s.AddLine(1, "CHEDDAR BLK", SuggestedConfidence.High, rawPayload: null,
                suggestedProductId: _productA, suggestedProductName: "Cheddar, Mild",
                suggestedAlternatives: alts));

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value.Lines);
        Assert.NotNull(line.SuggestedAlternatives);
        Assert.Equal(2, line.SuggestedAlternatives!.Count);
        Assert.Equal("Cheddar, Mild", line.SuggestedAlternatives[0].ProductName);
        Assert.Equal(0.88m, line.SuggestedAlternatives[0].Confidence);
        Assert.Equal(_productA, line.SuggestedAlternatives[0].ProductId);
        Assert.Equal("Cheddar, Sharp", line.SuggestedAlternatives[1].ProductName);
        Assert.Equal(0.62m, line.SuggestedAlternatives[1].Confidence);
    }

    [Fact]
    public async Task Line_with_only_one_alternative_maps_to_null()
    {
        var alts = new[] { new AlternativeCandidate(_productA, "Cheddar, Mild", 0.88m) };
        var session = BuildReadySession(s =>
            s.AddLine(1, "CHEDDAR BLK", SuggestedConfidence.High, rawPayload: null,
                suggestedAlternatives: alts));

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value.Lines);
        // Fewer than two alternatives — the block must not render.
        Assert.Null(line.SuggestedAlternatives);
    }

    [Fact]
    public async Task Line_without_alternatives_maps_to_null()
    {
        var session = BuildReadySession(s =>
            s.AddLine(1, "WHOLE MILK 2L", SuggestedConfidence.High, rawPayload: null));

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value.Lines);
        Assert.Null(line.SuggestedAlternatives);
    }

    [Fact]
    public async Task Three_alternatives_are_all_mapped_in_order()
    {
        var alts = new[]
        {
            new AlternativeCandidate(_productA, "Cheddar, Mild",   0.88m),
            new AlternativeCandidate(_productB, "Cheddar, Sharp",  0.62m),
            new AlternativeCandidate(_productC, "Cheddar, Marble", 0.41m),
        };
        var session = BuildReadySession(s =>
            s.AddLine(1, "CHEDDAR BLK", SuggestedConfidence.High, rawPayload: null,
                suggestedAlternatives: alts));

        var result = await Query(session).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var line = Assert.Single(result.Value.Lines);
        Assert.Equal(3, line.SuggestedAlternatives!.Count);
        // Order is preserved (best-first).
        Assert.Equal("Cheddar, Mild",   line.SuggestedAlternatives[0].ProductName);
        Assert.Equal("Cheddar, Sharp",  line.SuggestedAlternatives[1].ProductName);
        Assert.Equal("Cheddar, Marble", line.SuggestedAlternatives[2].ProductName);
    }
}
