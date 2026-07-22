using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1 unit tests for purchase-entry amendment (ADR-023, <c>docs/DomainDesign/purchase-entry-amendment.md</c>,
/// origin plantry-x3dy) — <see cref="ProductStock.AmendPurchase"/>. Covers the worked example from
/// spec §3 (the 2026-07-20 onions incident), the guard cases from spec §3/A4, repeat amendment
/// (A3), the idempotent zero-delta no-op (A10), and product-level closure by each of the three
/// Correction shapes (A5): counted-down (same lot), counted-up (new lot), and a manual fix-up
/// elsewhere on the product.
/// </summary>
public sealed class ProductStockAmendPurchaseTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static ProductStock NewStock(out MutableClock clock)
    {
        clock = new MutableClock();
        return ProductStock.Start(Household, Product, clock);
    }

    // ── Worked example (spec §3 — the 2026-07-20 onions incident) ────────────────

    [Fact(DisplayName = "Amending 1 lb to 3 lb appends one +2 Amendment row and adjusts the same lot to 3")]
    public void AmendPurchase_Worked_Example_Amends_Up()
    {
        var stock = NewStock(out var clock);
        var importLineId = Guid.NewGuid();
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));

        var result = stock.AmendPurchase(lot.Id, 3m, importLineId, User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(2m, result.Value);
        Assert.Equal(3m, lot.Quantity);
        Assert.False(lot.IsDepleted);

        Assert.Equal(2, stock.Journal.Count);
        var amendmentRow = Assert.Single(stock.Journal, j => j.Reason == StockReason.Amendment);
        Assert.Equal(+2m, amendmentRow.Delta);
        Assert.Equal(lot.Id, amendmentRow.StockEntryId);
        Assert.Equal(StockSourceType.Intake, amendmentRow.SourceType);
        Assert.Equal(importLineId, amendmentRow.SourceRef);
        Assert.Equal(User, amendmentRow.UserId);

        // The original Purchase row is never mutated.
        var purchaseRow = Assert.Single(stock.Journal, j => j.Reason == StockReason.Purchase);
        Assert.Equal(+1m, purchaseRow.Delta);
    }

    [Fact(DisplayName = "A second amendment (A3) appends a second compensating row against the new effective quantity")]
    public void AmendPurchase_Repeat_Amendment_Compounds_On_Effective_Quantity()
    {
        var stock = NewStock(out var clock);
        var importLineId = Guid.NewGuid();
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.AmendPurchase(lot.Id, 3m, importLineId, User, clock.Advance(TimeSpan.FromMinutes(1)));

        // Amend again, 3 lb -> 2.5 lb (a second, smaller correction).
        var result = stock.AmendPurchase(lot.Id, 2.5m, importLineId, User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(-0.5m, result.Value);
        Assert.Equal(2.5m, lot.Quantity);

        Assert.Equal(3, stock.Journal.Count);
        var amendmentRows = stock.Journal.Where(j => j.Reason == StockReason.Amendment).OrderBy(j => j.OccurredAt).ToList();
        Assert.Equal(2, amendmentRows.Count);
        Assert.Equal(+2m, amendmentRows[0].Delta);
        Assert.Equal(-0.5m, amendmentRows[1].Delta);
    }

    [Fact(DisplayName = "A second DOWNWARD amendment is not blocked by the prior amendment's own compensating delta (A3)")]
    public void AmendPurchase_Repeat_Downward_Amendment_Not_Blocked_By_Prior_Amendment_Delta()
    {
        // Regression: the consumed-total guard must count only TRUE consumption (Consumed /
        // Discarded / negative Correction), never a prior Amendment's own negative delta — nothing
        // was actually consumed here, so a further reduction must succeed.
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1))); // 5 -> 3 (Amendment -2)

        var result = stock.AmendPurchase(lot.Id, 1.5m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1))); // 3 -> 1.5

        Assert.True(result.IsSuccess);
        Assert.Equal(-1.5m, result.Value);
        Assert.Equal(1.5m, lot.Quantity);
    }

    // ── Guard: corrected quantity must be positive ────────────────────────────────

    [Theory(DisplayName = "A non-positive corrected quantity is rejected")]
    [InlineData(0)]
    [InlineData(-1)]
    public void AmendPurchase_Rejects_NonPositive_CorrectedQuantity(int correctedQuantity)
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock);

        var result = stock.AmendPurchase(lot.Id, correctedQuantity, Guid.NewGuid(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.InvalidAmendQuantity", result.Error.Code);
    }

    // ── Guard: lot must exist / must be an intake lot ─────────────────────────────

    [Fact(DisplayName = "Amending an unknown lot fails with LotNotFound")]
    public void AmendPurchase_Rejects_Unknown_Lot()
    {
        var stock = NewStock(out var clock);
        stock.AddStock(1m, Unit, Location, User, clock);

        var result = stock.AmendPurchase(StockEntryId.New(), 3m, Guid.NewGuid(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "Amending a non-intake lot (no Purchase row) fails with LotNotFromIntake")]
    public void AmendPurchase_Rejects_Lot_With_No_Purchase_Row()
    {
        var stock = NewStock(out var clock);
        // A Correction-only lot never had a Purchase row (P4-1 Take Stock discovery).
        var lot = stock.AddStock(4m, Unit, Location, User, clock, reason: StockReason.Correction);

        var result = stock.AmendPurchase(lot.Id, 5m, Guid.NewGuid(), User, clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotFromIntake", result.Error.Code);
    }

    // ── Guard: corrected quantity cannot go below what was already consumed ──────

    [Fact(DisplayName = "spec §3 guard: corrected quantity below the consumed-from-lot total is rejected")]
    public void AmendPurchase_Rejects_CorrectedQuantity_Below_Consumed()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(3m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.Consume(2.5m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        var result = stock.AmendPurchase(lot.Id, 2m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AmendBelowConsumed", result.Error.Code);
        Assert.Equal(0.5m, lot.Quantity); // untouched
    }

    [Fact(DisplayName = "Amending down to exactly the consumed total succeeds and drains the lot")]
    public void AmendPurchase_Allows_CorrectedQuantity_Equal_To_Consumed()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(3m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.Consume(2.5m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        var result = stock.AmendPurchase(lot.Id, 2.5m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(-0.5m, result.Value);
        Assert.Equal(0m, lot.Quantity);
        Assert.True(lot.IsDepleted);
    }

    // ── Guard: lot must be active (not depleted) ──────────────────────────────────

    [Fact(DisplayName = "Amending a fully-depleted lot fails with LotNotActive")]
    public void AmendPurchase_Rejects_Depleted_Lot()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(2m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.Consume(2m, Unit, StockReason.Consumed, new IdentityQuantityConverter(), User, clock.Advance(TimeSpan.FromMinutes(1)));
        Assert.True(lot.IsDepleted);

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.LotNotActive", result.Error.Code);
    }

    // ── Guard: closed by a product-level Correction dated after the purchase (A4-iv / A5) ────

    [Fact(DisplayName = "Closed by a counted-down Correction on the SAME lot dated after the purchase")]
    public void AmendPurchase_Closed_By_CountedDown_Correction_On_Same_Lot()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        // A Take Stock walk counts down: fewer on the shelf than recorded — negative Correction, same lot.
        stock.Consume(
            0.2m, Unit, StockReason.Correction, new IdentityQuantityConverter(), User,
            clock.Advance(TimeSpan.FromMinutes(1)), targetEntry: lot.Id);

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AmendmentClosedByCorrection", result.Error.Code);
    }

    [Fact(DisplayName = "Closed by a counted-up Correction landing as a NEW lot on the same product")]
    public void AmendPurchase_Closed_By_CountedUp_Correction_On_New_Lot()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        // A Take Stock walk counts up: more on the shelf than recorded — positive Correction lands
        // as a brand-new lot (never touching the original), per A5.
        stock.AddStock(2m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), reason: StockReason.Correction);

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AmendmentClosedByCorrection", result.Error.Code);
    }

    [Fact(DisplayName = "Closed by a manual fix-up Correction on a DIFFERENT lot of the same product (A5, product-level)")]
    public void AmendPurchase_Closed_By_Manual_FixUp_Correction_On_Different_Lot()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        var otherLot = stock.AddStock(5m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        // A manual fix-up removes phantom stock from the OTHER lot, after the purchase being amended.
        stock.Consume(
            1m, Unit, StockReason.Correction, new IdentityQuantityConverter(), User,
            clock.Advance(TimeSpan.FromMinutes(1)), targetEntry: otherLot.Id);

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AmendmentClosedByCorrection", result.Error.Code);
    }

    [Fact(DisplayName = "A Correction dated BEFORE the purchase does not close the amendment")]
    public void AmendPurchase_Not_Closed_By_Correction_Before_Purchase()
    {
        var stock = NewStock(out var clock);
        // Correction lot created first (an earlier walk), then the purchase being amended.
        stock.AddStock(2m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)), reason: StockReason.Correction);
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(2m, result.Value);
    }

    // ── Idempotent zero-delta no-op (A10) ─────────────────────────────────────────

    [Fact(DisplayName = "Amending to the already-effective quantity is a zero-delta no-op success")]
    public void AmendPurchase_Zero_Delta_Is_Idempotent_NoOp()
    {
        var stock = NewStock(out var clock);
        var lot = stock.AddStock(3m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));

        var result = stock.AmendPurchase(lot.Id, 3m, Guid.NewGuid(), User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value);
        Assert.Equal(3m, lot.Quantity);
        Assert.Single(stock.Journal); // no Amendment row appended
    }

    [Fact(DisplayName = "A re-driven amendment to a quantity already applied is a zero-delta no-op, not a double-write")]
    public void AmendPurchase_ReDrive_Same_CorrectedQuantity_Is_NoOp()
    {
        var stock = NewStock(out var clock);
        var importLineId = Guid.NewGuid();
        var lot = stock.AddStock(1m, Unit, Location, User, clock.Advance(TimeSpan.FromMinutes(1)));
        stock.AmendPurchase(lot.Id, 3m, importLineId, User, clock.Advance(TimeSpan.FromMinutes(1)));

        // Re-run (e.g. AmendCommittedLineCommand retried after a mid-sequence pricing failure) with
        // the SAME corrected quantity — must not double-write.
        var result = stock.AmendPurchase(lot.Id, 3m, importLineId, User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value);
        Assert.Equal(2, stock.Journal.Count); // Purchase + the one Amendment row, no second row
    }
}
