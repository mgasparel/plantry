using CsCheck;
using Plantry.Inventory.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Inventory.Domain;

/// <summary>
/// L1b property-based tests (CsCheck) for the invariants that must hold for *all* inputs
/// (PHASE-1-PLAN.md): the journal is a faithful ledger (Σ deltas == quantity on hand), consume
/// never over-deducts, and FEFO is a correctly-sorted total order.
/// </summary>
public sealed class ProductStockPropertyTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Location = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static readonly DateOnly Epoch = new(2026, 1, 1);

    // An operation is either intake (+amount) or a consume (−amount, same unit so an identity converter applies).
    private readonly record struct Op(bool IsAdd, decimal Amount, int? ExpiryOffsetDays);

    private static readonly Gen<Op> GenOp =
        Gen.Select(Gen.Bool, Gen.Decimal[0.001m, 1000m], Gen.Int[0, 60].Nullable())
           .Select(t => new Op(t.Item1, Math.Round(t.Item2, 3), t.Item3));

    private static readonly Gen<List<Op>> GenOps = GenOp.List[1, 40].Select(l => l.ToList());

    [Fact(DisplayName = "Σ journal deltas always equals the quantity remaining across all lots")]
    public void Journal_Is_A_Faithful_Ledger_Of_Quantity_On_Hand()
    {
        GenOps.Sample(ops =>
        {
            var clock = new MutableClock();
            var converter = new IdentityQuantityConverter();
            var stock = ProductStock.Start(Household, Product, clock);

            foreach (var op in ops)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                if (op.IsAdd || stock.Entries.All(e => !e.IsActive))
                {
                    var expiry = op.ExpiryOffsetDays is { } d ? Epoch.AddDays(d) : (DateOnly?)null;
                    stock.AddStock(op.Amount, Unit, Location, User, clock, expiryDate: expiry);
                }
                else
                {
                    var result = stock.Consume(op.Amount, Unit, StockReason.Consumed, converter, User, clock);
                    Assert.True(result.IsSuccess);
                }
            }

            var ledger = stock.Journal.Sum(j => j.Delta);
            var onHand = stock.Entries.Sum(e => e.Quantity);
            Assert.Equal(onHand, ledger);
            Assert.All(stock.Entries, e => Assert.True(e.Quantity >= 0m)); // never over-deducted
        });
    }

    [Fact(DisplayName = "ActiveLotsFefo is a total order: every adjacent pair is correctly sorted, deterministically")]
    public void Fefo_Is_A_Correctly_Sorted_Total_Order()
    {
        GenOps.Sample(ops =>
        {
            var clock = new MutableClock();
            var stock = ProductStock.Start(Household, Product, clock);

            foreach (var op in ops)
            {
                // Deliberately do *not* advance the clock every time, so some lots share created_at
                // and must fall back to the entry_id tiebreaker.
                if (op.ExpiryOffsetDays is { } _ || op.IsAdd) clock.Advance(TimeSpan.FromTicks(op.IsAdd ? 1_000 : 0));
                var expiry = op.ExpiryOffsetDays is { } d ? Epoch.AddDays(d) : (DateOnly?)null;
                stock.AddStock(op.Amount, Unit, Location, User, clock, expiryDate: expiry);
            }

            var ordered = stock.ActiveLotsFefo().ToList();

            for (var i = 1; i < ordered.Count; i++)
                Assert.True(Precedes(ordered[i - 1], ordered[i]),
                    $"FEFO order violated at {i}: {Describe(ordered[i - 1])} should precede {Describe(ordered[i])}");

            // Determinism: re-ordering the same aggregate yields the identical id sequence.
            Assert.Equal(ordered.Select(e => e.Id), stock.ActiveLotsFefo().Select(e => e.Id));
        });
    }

    // The documented comparator: expiry ASC NULLS LAST, then created_at ASC, then entry_id ASC.
    private static bool Precedes(StockEntry a, StockEntry b)
    {
        var ax = a.ExpiryDate is null;
        var bx = b.ExpiryDate is null;
        if (ax != bx) return !ax;                       // a non-null expiry precedes a null one
        if (!ax && a.ExpiryDate != b.ExpiryDate) return a.ExpiryDate < b.ExpiryDate;
        if (a.CreatedAt != b.CreatedAt) return a.CreatedAt < b.CreatedAt;
        return a.Id.Value.CompareTo(b.Id.Value) <= 0;
    }

    private static string Describe(StockEntry e) => $"(expiry={e.ExpiryDate?.ToString() ?? "null"}, created={e.CreatedAt:O}, id={e.Id})";
}
