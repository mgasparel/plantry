using Plantry.Recipes.Application;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L1 tests for <see cref="AddToShoppingLabelCalculator"/> (plantry-gsj) — the pure derivation of the
/// recipe Detail add-button label state (Fresh / Partial / Covered) and pending-line count from the
/// delta between a target set and the recipe's existing contribution slice.
/// </summary>
public sealed class AddToShoppingLabelCalculatorTests
{
    private static readonly Guid UnitId = Guid.CreateVersion7();

    private static ShoppingItem Target(Guid productId, decimal qty) => new(productId, qty, UnitId);

    [Fact(DisplayName = "Nothing contributed yet → Fresh with pending = target count")]
    public void NothingContributed_IsFresh()
    {
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();
        var targets = new[] { Target(p1, 2m), Target(p2, 3m) };

        var label = AddToShoppingLabelCalculator.Compute(
            targets, new Dictionary<Guid, decimal>(), new HashSet<Guid>());

        Assert.Equal(AddButtonState.Fresh, label.State);
        Assert.Equal(2, label.PendingLines);
    }

    [Fact(DisplayName = "Recipe slice already covers every target → Covered with zero pending")]
    public void FullyCovered_IsCovered()
    {
        var p1 = Guid.CreateVersion7();
        var targets = new[] { Target(p1, 2m) };
        var contributed = new Dictionary<Guid, decimal> { [p1] = 2m };

        var label = AddToShoppingLabelCalculator.Compute(targets, contributed, new HashSet<Guid>());

        Assert.Equal(AddButtonState.Covered, label.State);
        Assert.Equal(0, label.PendingLines);
    }

    [Fact(DisplayName = "Slice below the target (servings grew) → Partial with the shortfall line count")]
    public void ContributedButBelowTarget_IsPartial()
    {
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();
        // p1 already fully covered, p2 contributed but now short → 1 pending, and a slice exists → Partial.
        var targets = new[] { Target(p1, 2m), Target(p2, 5m) };
        var contributed = new Dictionary<Guid, decimal> { [p1] = 2m, [p2] = 3m };

        var label = AddToShoppingLabelCalculator.Compute(targets, contributed, new HashSet<Guid>());

        Assert.Equal(AddButtonState.Partial, label.State);
        Assert.Equal(1, label.PendingLines);
    }

    [Fact(DisplayName = "A checked-off product counts as covered (no resurrection)")]
    public void CheckedOffProduct_CountsAsCovered()
    {
        var p1 = Guid.CreateVersion7();
        var targets = new[] { Target(p1, 2m) };
        var checkedOff = new HashSet<Guid> { p1 };

        var label = AddToShoppingLabelCalculator.Compute(
            targets, new Dictionary<Guid, decimal>(), checkedOff);

        Assert.Equal(AddButtonState.Covered, label.State);
        Assert.Equal(0, label.PendingLines);
    }

    [Fact(DisplayName = "Some covered, one brand-new pending with an existing slice → Partial 'Add 1 more'")]
    public void MixedCoverageWithExistingSlice_IsPartial()
    {
        var covered = Guid.CreateVersion7();
        var fresh = Guid.CreateVersion7();
        var targets = new[] { Target(covered, 1m), Target(fresh, 4m) };
        // Recipe already contributed the covered product; the fresh one has no slice.
        var contributed = new Dictionary<Guid, decimal> { [covered] = 1m };

        var label = AddToShoppingLabelCalculator.Compute(targets, contributed, new HashSet<Guid>());

        Assert.Equal(AddButtonState.Partial, label.State);
        Assert.Equal(1, label.PendingLines);
    }

    [Fact(DisplayName = "Empty target set → Covered with zero pending (nothing to add)")]
    public void EmptyTargets_IsCovered()
    {
        var label = AddToShoppingLabelCalculator.Compute(
            [], new Dictionary<Guid, decimal>(), new HashSet<Guid>());

        Assert.Equal(AddButtonState.Covered, label.State);
        Assert.Equal(0, label.PendingLines);
    }
}
