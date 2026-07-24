using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1/L2 tests for <see cref="AmendCommittedLineCommand"/> over fake ports (ADR-023 A10): ordering
/// (stock -> price -> MarkAmended -> save), the A8 skip for a weight-priced line's each-count fix, error
/// propagation from either port, and the structural eligibility guards (not committed / no linked lot / no
/// resolved product) that belong to the orchestrator rather than to Inventory's own ledger-semantic guards.
/// </summary>
public sealed class AmendCommittedLineCommandTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _actingUserId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private ImportSession ReadySession()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        session.MarkReady("Superstore", Clock.UtcNow);
        return session;
    }

    private AmendCommittedLineCommand Amend(
        Guid lineId, decimal correctedQuantity, FakeImportSessionRepository repo,
        FakeAmendStockPort stock, FakeAmendPricePort price) =>
        new(lineId, correctedQuantity, _actingUserId, repo, stock, price, Clock,
            new FakeTenantContext(_household), NullLogger<AmendCommittedLineCommand>.Instance);

    private (FakeImportSessionRepository Repo, ImportLine Line, Guid ProductId, Guid StockEntryId, Guid PriceObservationId)
        CommittedNonWeightLine(decimal quantity = 1m, decimal? price = 3.98m)
    {
        var session = ReadySession();
        var line = session.AddLine(1, "ONIONS YELLOW", SuggestedConfidence.High, null,
            suggestedQuantity: quantity, suggestedUnitLabel: "lb", suggestedPrice: price);
        var productId = Guid.CreateVersion7();
        line.Confirm(productId, null, quantity, _unitId, _locationId, null, price);
        var stockEntryId = Guid.CreateVersion7();
        var priceObservationId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, priceObservationId);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        return (repo, line, productId, stockEntryId, priceObservationId);
    }

    [Fact]
    public async Task Amends_Stock_Then_Price_Then_Marks_The_Line_And_Saves()
    {
        var (repo, line, productId, stockEntryId, priceObservationId) = CommittedNonWeightLine();
        var stock = new FakeAmendStockPort { DeltaToReturn = 2m };
        var price = new FakeAmendPricePort();

        var result = await Amend(line.Id.Value, 3m, repo, stock, price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var stockCall = Assert.Single(stock.Calls);
        Assert.Equal((productId, stockEntryId, 3m, line.Id.Value, _actingUserId), stockCall);
        var priceCall = Assert.Single(price.Calls);
        Assert.Equal((priceObservationId, 3m, _actingUserId), priceCall);
        Assert.Equal(3m, line.AmendedQuantity);
        Assert.Equal(Clock.UtcNow, line.AmendedAt);
        // PriceObservationId advances to the port's returned (new live) id — not left pointing at the
        // now-superseded original — so a subsequent amendment chains off the right row (ADR-023 A7/A10).
        Assert.NotEqual(priceObservationId, line.PriceObservationId);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Skips_The_Price_Leg_For_A_WeightPriced_Lines_EachCount_Fix_ADR_023_A8()
    {
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
        line.Confirm(productId, null, 7m, _unitId, _locationId, null, price: 0.79m);
        var stockEntryId = Guid.CreateVersion7();
        var priceObservationId = Guid.CreateVersion7();
        line.MarkCommitted(stockEntryId, priceObservationId);
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var stock = new FakeAmendStockPort();
        var price = new FakeAmendPricePort();

        var result = await Amend(line.Id.Value, 9m, repo, stock, price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(stock.Calls);   // stock still amends the each-count
        Assert.Empty(price.Calls);    // but price is left untouched (weight-denominated)
        Assert.Equal(9m, line.AmendedQuantity);
        Assert.Equal(priceObservationId, line.PriceObservationId); // unchanged — A8 skip
    }

    [Fact]
    public async Task Propagates_The_Inventory_Guard_Error_And_Never_Calls_Price_Or_Marks_The_Line()
    {
        var (repo, line, _, _, _) = CommittedNonWeightLine();
        var guardError = Error.Custom("Inventory.AmendBelowConsumed", "Corrected quantity cannot be less than the 2 already consumed from this lot.");
        var stock = new FakeAmendStockPort { FailWith = guardError };
        var price = new FakeAmendPricePort();

        var result = await Amend(line.Id.Value, 0.5m, repo, stock, price).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AmendBelowConsumed", result.Error.Code);
        Assert.Empty(price.Calls);
        Assert.Null(line.AmendedQuantity);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Propagates_The_Pricing_Error_And_Never_Marks_The_Line()
    {
        var (repo, line, _, _, _) = CommittedNonWeightLine();
        var stock = new FakeAmendStockPort();
        var price = new FakeAmendPricePort { FailWith = Error.Custom("Pricing.RecordAmendedObservation.OriginalNotFound", "not found") };

        var result = await Amend(line.Id.Value, 3m, repo, stock, price).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Pricing.RecordAmendedObservation.OriginalNotFound", result.Error.Code);
        Assert.Null(line.AmendedQuantity);
        Assert.Equal(0, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task Fails_When_The_Line_Does_Not_Exist()
    {
        var repo = new FakeImportSessionRepository();

        var result = await Amend(Guid.CreateVersion7(), 3m, repo, new FakeAmendStockPort(), new FakeAmendPricePort())
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.NotFound.Code, result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_The_Line_Is_Not_Committed()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Flour", SuggestedConfidence.High, null); // still Pending
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Amend(line.Id.Value, 3m, repo, new FakeAmendStockPort(), new FakeAmendPricePort())
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotCommitted", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_The_Line_Has_No_Resolved_Product()
    {
        // A new-product line whose CreatedProductId was never stamped (defensive — MarkCommitted normally
        // always supplies it for a new-product line) leaves ProductId and CreatedProductId both null.
        var session = ReadySession();
        var line = session.AddLine(1, "Mystery Item", SuggestedConfidence.High, null);
        line.ConfirmAsNew("Mystery Item", Guid.CreateVersion7(), 1m, _unitId, _locationId, null, 1.99m);
        line.MarkCommitted(Guid.CreateVersion7(), null, createdProductId: null);
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Amend(line.Id.Value, 3m, repo, new FakeAmendStockPort(), new FakeAmendPricePort())
            .ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineMissingProduct", result.Error.Code);
    }

    [Fact]
    public async Task Unauthorized_When_No_Household_On_Tenant_Context()
    {
        var (repo, line, _, _, _) = CommittedNonWeightLine();
        var command = new AmendCommittedLineCommand(
            line.Id.Value, 3m, _actingUserId, repo, new FakeAmendStockPort(), new FakeAmendPricePort(),
            Clock, new FakeTenantContext(null), NullLogger<AmendCommittedLineCommand>.Instance);

        var result = await command.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(Error.Unauthorized.Code, result.Error.Code);
    }
}
