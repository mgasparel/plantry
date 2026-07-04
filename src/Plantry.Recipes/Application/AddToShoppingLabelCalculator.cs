namespace Plantry.Recipes.Application;

/// <summary>
/// Pure, server-side derivation of the recipe Detail "Add missing" / "Add all" button label state
/// from the true delta between a target set and what this recipe has already contributed to the
/// shopping list (plantry-gsj). Kept out of the view so the three-way state is unit-testable at L1 and
/// the button can never silently offer a full re-add.
///
/// <para>Given the button's target lines (shortfall for "Add missing", full required set for "Add all")
/// and the recipe's current contribution state, each target is classified:</para>
/// <list type="bullet">
///   <item><description><b>Covered</b> — the recipe's own slice already meets the target quantity, OR the
///     product only has a checked-off row (a completed intent the sync will not resurrect).</description></item>
///   <item><description><b>Pending</b> — the slice is absent or below the target (needs an add or top-up).</description></item>
/// </list>
/// The label state is then:
/// <list type="bullet">
///   <item><description><see cref="AddButtonState.Covered"/> — no pending lines ("Added", disabled).</description></item>
///   <item><description><see cref="AddButtonState.Partial"/> — some lines pending AND the recipe already has a
///     slice among the targets, i.e. servings grew ("Add N more").</description></item>
///   <item><description><see cref="AddButtonState.Fresh"/> — lines pending and the recipe has contributed nothing
///     yet ("Add N missing" / "Add N ingredients").</description></item>
/// </list>
/// </summary>
public static class AddToShoppingLabelCalculator
{
    public static AddButtonLabel Compute(
        IReadOnlyList<ShoppingItem> targets,
        IReadOnlyDictionary<Guid, decimal> contributedByProduct,
        IReadOnlySet<Guid> checkedOffProducts)
    {
        var pending = 0;
        var hasExistingContribution = false;

        foreach (var target in targets)
        {
            if (contributedByProduct.TryGetValue(target.ProductId, out var contributed))
            {
                hasExistingContribution = true;

                // Covered when the recipe's own slice already meets the target quantity.
                // (Same-unit comparison — the recipe emits its stable per-product unit.)
                if (contributed + Tolerance >= target.Quantity)
                    continue;

                pending++;
            }
            else if (checkedOffProducts.Contains(target.ProductId))
            {
                // Completed intent — treated as covered, never resurrected.
                continue;
            }
            else
            {
                pending++;
            }
        }

        var state = pending == 0
            ? AddButtonState.Covered
            : hasExistingContribution
                ? AddButtonState.Partial
                : AddButtonState.Fresh;

        return new AddButtonLabel(state, pending);
    }

    /// <summary>Rounding tolerance so a slice equal to the target within quantity precision counts as covered.</summary>
    private const decimal Tolerance = 0.0005m;
}

/// <summary>The three-way state of a recipe "add to shopping list" button (plantry-gsj).</summary>
public enum AddButtonState
{
    /// <summary>Nothing of this recipe's target is on the list yet — a fresh add ("Add N missing").</summary>
    Fresh,

    /// <summary>Some target already contributed but the shortfall grew (servings up) — a top-up ("Add N more").</summary>
    Partial,

    /// <summary>The recipe's slice already covers the whole target — nothing to do ("Added", disabled).</summary>
    Covered,
}

/// <summary>
/// The computed label state plus the number of lines the sync would still add or top up
/// (<see cref="PendingLines"/>), i.e. the N in "Add N missing" / "Add N more" (plantry-gsj).
/// Zero when <see cref="State"/> is <see cref="AddButtonState.Covered"/>.
/// </summary>
public sealed record AddButtonLabel(AddButtonState State, int PendingLines);
