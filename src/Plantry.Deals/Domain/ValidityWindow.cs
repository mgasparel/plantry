using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// A flyer/deal's run dates (DD10). The invariant <c>ValidFrom &lt;= ValidTo</c> is enforced at
/// construction (<see cref="Create"/> returns a <see cref="Result{T}"/>) and mirrored as a DB CHECK
/// on <c>valid_from &lt;= valid_to</c>. Copied from a <see cref="FlyerImport"/> onto each
/// <see cref="Deal"/> and projected unchanged onto the deal's price observation (D9).
/// </summary>
public sealed class ValidityWindow : ValueObject
{
    public static readonly Error InvalidRange =
        Error.Custom("Deals.ValidityWindow.InvalidRange", "valid_from must be on or before valid_to.");

    public DateOnly ValidFrom { get; private set; }
    public DateOnly ValidTo { get; private set; }

    private ValidityWindow() { } // EF

    private ValidityWindow(DateOnly validFrom, DateOnly validTo)
    {
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    /// <summary>Constructs a window, rejecting an inverted range (DD10).</summary>
    public static Result<ValidityWindow> Create(DateOnly validFrom, DateOnly validTo) =>
        validFrom <= validTo
            ? new ValidityWindow(validFrom, validTo)
            : InvalidRange;

    /// <summary>True when <paramref name="today"/> falls within the window (inclusive) — the in-window half of DD7.</summary>
    public bool Contains(DateOnly today) => ValidFrom <= today && today <= ValidTo;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return ValidFrom;
        yield return ValidTo;
    }
}
