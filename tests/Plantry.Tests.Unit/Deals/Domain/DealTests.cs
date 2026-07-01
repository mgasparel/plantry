using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Domain;

/// <summary>
/// L1 unit tests for the <see cref="Deal"/> aggregate (§5): status transitions (Pending →
/// Confirmed/Rejected, invalid transitions rejected), the DD1 <c>ProductId</c> invariant, and the
/// commit-linkage guard.
/// </summary>
public sealed class DealTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly FlyerImportId Import = FlyerImportId.New();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)).Value;

    private static RawDeal Raw() =>
        new("Whole Milk 2L", "Dairyland", "2L", 3.99m, 2m, Guid.NewGuid(), "Save $1", Window());

    private static Deal Stage(MatchProposal? proposal = null, TestClock? clock = null) =>
        Deal.Stage(
            Household, Import, Store, Raw(),
            DealNormalizer.Normalize("Whole Milk 2L"),
            proposal ?? MatchProposal.Unmatched(),
            clock ?? new TestClock());

    [Fact(DisplayName = "Stage materializes a Pending deal with the ACL quarantine populated")]
    public void Stage_CreatesPending()
    {
        var proposal = new MatchProposal(Product, MatchConfidence.Low, "looks like milk");
        var deal = Stage(proposal);

        Assert.Equal(DealStatus.Pending, deal.Status);
        Assert.Equal(DealSource.Flyer, deal.Source);
        Assert.Equal("whole milk", deal.NormalizedName);
        Assert.Equal(Product, deal.SuggestedProductId);
        Assert.Equal(MatchConfidence.Low, deal.MatchConfidence);
        Assert.Null(deal.ProductId);
        Assert.False(deal.AutoMatched);
    }

    [Fact(DisplayName = "Confirm: Pending → Confirmed sets ProductId and reviewer (DD1)")]
    public void Confirm_SetsProductAndReviewer()
    {
        var clock = new TestClock();
        var deal = Stage(clock: clock);

        var result = deal.Confirm(Product, User, clock.Advance(TimeSpan.FromMinutes(1)));

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(Product, deal.ProductId);
        Assert.Equal(User, deal.ReviewedByUserId);
        Assert.Equal(clock.UtcNow, deal.ReviewedAt);
        Assert.False(deal.AutoMatched);
    }

    [Fact(DisplayName = "AutoConfirm (memory path): Pending → Confirmed, AutoMatched, High, no reviewer")]
    public void AutoConfirm_MarksAutoMatched()
    {
        var deal = Stage();

        var result = deal.AutoConfirm(Product, new TestClock());

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(Product, deal.ProductId);
        Assert.True(deal.AutoMatched);
        Assert.Equal(MatchConfidence.High, deal.MatchConfidence);
        Assert.Null(deal.ReviewedByUserId);
    }

    [Fact(DisplayName = "Reject: Pending → Rejected clears ProductId and writes no observation (DD1/D5)")]
    public void Reject_ClearsProduct()
    {
        var deal = Stage();

        var result = deal.Reject(User, new TestClock());

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Rejected, deal.Status);
        Assert.Null(deal.ProductId);
        Assert.Null(deal.CommittedPriceObservationId);
    }

    [Fact(DisplayName = "Correct on an auto-confirmed deal re-resolves and clears AutoMatched (DJ4)")]
    public void Correct_OnConfirmed_ReResolves()
    {
        var deal = Stage();
        deal.AutoConfirm(Product, new TestClock());

        var newProduct = Guid.NewGuid();
        var result = deal.Correct(newProduct, User, new TestClock());

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(newProduct, deal.ProductId);
        Assert.False(deal.AutoMatched);
        Assert.Equal(User, deal.ReviewedByUserId);
    }

    [Fact(DisplayName = "Invalid transition: a Rejected deal cannot be Confirmed or Corrected")]
    public void RejectedDeal_CannotBeResolved()
    {
        var deal = Stage();
        deal.Reject(User, new TestClock());

        var confirm = deal.Confirm(Product, User, new TestClock());
        var correct = deal.Correct(Product, User, new TestClock());

        Assert.True(confirm.IsFailure);
        Assert.Equal(Deal.AlreadyRejected, confirm.Error);
        Assert.True(correct.IsFailure);
        Assert.Equal(DealStatus.Rejected, deal.Status);
    }

    [Fact(DisplayName = "Invalid transition: a Confirmed deal cannot be re-Confirmed")]
    public void ConfirmedDeal_CannotBeReConfirmed()
    {
        var deal = Stage();
        deal.Confirm(Product, User, new TestClock());

        var again = deal.Confirm(Guid.NewGuid(), User, new TestClock());

        Assert.True(again.IsFailure);
        Assert.Equal(Deal.NotResolvable, again.Error);
    }

    [Fact(DisplayName = "AutoConfirm is rejected once the deal is no longer Pending")]
    public void AutoConfirm_OnlyFromPending()
    {
        var deal = Stage();
        deal.Confirm(Product, User, new TestClock());

        var result = deal.AutoConfirm(Guid.NewGuid(), new TestClock());

        Assert.True(result.IsFailure);
        Assert.Equal(Deal.NotResolvable, result.Error);
    }

    [Fact(DisplayName = "LinkObservation records the committed observation only when Confirmed (DD2)")]
    public void LinkObservation_RequiresConfirmed()
    {
        var deal = Stage();

        var pendingLink = deal.LinkObservation(Guid.NewGuid(), new TestClock());
        Assert.True(pendingLink.IsFailure);
        Assert.Equal(Deal.NotConfirmed, pendingLink.Error);

        deal.Confirm(Product, User, new TestClock());
        var obs = Guid.NewGuid();
        var confirmedLink = deal.LinkObservation(obs, new TestClock());

        Assert.True(confirmedLink.IsSuccess);
        Assert.Equal(obs, deal.CommittedPriceObservationId);
    }

    [Fact(DisplayName = "Confirm is permitted after the window has closed (backfill, DD14)")]
    public void Confirm_AllowedPastWindow()
    {
        // The aggregate does not gate on the clock vs window — the in-window rule is a read-model
        // (queue) predicate; confirming an expired deal is an explicit price-history backfill.
        var deal = Stage();

        var result = deal.Confirm(Product, User, new TestClock(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(result.IsSuccess);
        Assert.Equal(DealStatus.Confirmed, deal.Status);
    }
}
