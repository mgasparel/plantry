using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L2 tests for <see cref="GetCommittedSessionDetailQuery"/> — receipt-intake-history.md H7/H8: the
/// Committed-only state guard, receipt-order line projection, dismissed-line handling, and the
/// new-product/each-estimate badge inputs. The query takes an already-loaded <see cref="ImportSession"/>
/// (plantry-ubqb — the caller fetches it once for its own state guard rather than the query re-fetching
/// by id), so "unknown session" is the caller's <c>NotFound</c> concern, not this query's.
/// </summary>
public sealed class GetCommittedSessionDetailQueryTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _categoryId = Guid.CreateVersion7();

    private ImportSession NewSession() =>
        ImportSession.Start(HouseholdId.From(_householdId), ImportSourceType.Receipt, _userId, Clock);

    [Fact]
    public async Task Returns_lines_in_receipt_order()
    {
        var session = NewSession();
        var line3 = session.AddLine(3, "Third", SuggestedConfidence.High, null);
        var line1 = session.AddLine(1, "First", SuggestedConfidence.High, null);
        var line2 = session.AddLine(2, "Second", SuggestedConfidence.High, null);
        session.MarkReady("Store", Clock.UtcNow);
        foreach (var l in new[] { line1, line2, line3 })
        {
            l.Confirm(_productId, null, 1m, _unitId, _locationId, null, 1m);
            l.MarkCommitted(Guid.NewGuid(), null);
        }
        session.MarkCommitted(Clock.UtcNow);

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(_householdId))
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(["First", "Second", "Third"], result.Value.Lines.Select(l => l.ReceiptText));
    }

    [Fact]
    public async Task Dismissed_line_stays_in_the_result_flagged_and_the_new_product_badge_is_set()
    {
        var session = NewSession();
        var dismissed = session.AddLine(1, "Loyalty scan", SuggestedConfidence.None, null, suggestedPrice: 65.00m);
        var newProduct = session.AddLine(2, "Sourdough", SuggestedConfidence.None, null);
        session.MarkReady("Store", Clock.UtcNow);
        dismissed.Dismiss();
        newProduct.ConfirmAsNew("Sourdough Loaf", _categoryId, 1m, _unitId, _locationId, null, 5.25m);
        newProduct.MarkCommitted(Guid.NewGuid(), null, createdProductId: Guid.NewGuid());
        session.MarkCommitted(Clock.UtcNow);

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(_householdId))
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        var dismissedRow = result.Value.Lines.Single(l => l.ReceiptText == "Loyalty scan");
        Assert.True(dismissedRow.IsDismissed);
        Assert.Null(dismissedRow.Quantity); // never confirmed — nothing to show but "—"
        Assert.Equal(65.00m, dismissedRow.Price); // falls back to the AI-suggested price

        var newProductRow = result.Value.Lines.Single(l => l.ReceiptText == "Sourdough");
        Assert.False(newProductRow.IsDismissed);
        Assert.True(newProductRow.CreatedProductId.HasValue);
        Assert.Null(newProductRow.ProductId); // new-product lines never back-fill ProductId
    }

    [Fact]
    public async Task Each_estimate_flag_carries_through()
    {
        var session = NewSession();
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, null,
            suggestedProductId: _productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
        session.MarkReady("Store", Clock.UtcNow);
        line.Confirm(_productId, null, 7m, _unitId, _locationId, null, 0.79m);
        line.MarkCommitted(Guid.NewGuid(), null);
        session.MarkCommitted(Clock.UtcNow);

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(_householdId))
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(Assert.Single(result.Value.Lines).HasEachEstimate);
    }

    // ── State guard (H7) ────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_failure_for_a_ready_session()
    {
        var session = NewSession();
        session.AddLine(1, "Item", SuggestedConfidence.High, null);
        session.MarkReady("Store", Clock.UtcNow);

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(_householdId))
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotCommitted", result.Error.Code);
    }

    [Fact]
    public async Task Returns_failure_for_a_discarded_session()
    {
        var session = NewSession();
        session.MarkReady("Store", Clock.UtcNow);
        session.Discard();

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(_householdId))
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotCommitted", result.Error.Code);
    }

    [Fact]
    public async Task Returns_unauthorized_when_no_household_in_context()
    {
        var session = NewSession();
        session.MarkReady("Store", Clock.UtcNow);
        session.MarkCommitted(Clock.UtcNow);

        var result = await new GetCommittedSessionDetailQuery(session, new FakeTenantContext(null))
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized.Code, result.Error.Code);
    }
}
