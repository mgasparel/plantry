using Plantry.Intake.Domain;

namespace Plantry.Intake.Application;

/// <summary>
/// The server-side prefill priority chain for the intake review form (ADR-020 §3, Boundary judgment
/// call 1: this is domain, it stays server-side and is never re-derived in the island). Lives in the
/// Application layer so both callers reach it: the review page's hydration builder (<c>ReviewModel</c>)
/// and <see cref="CommitSessionCommand"/>'s commit-time auto-confirm of high-confidence lines
/// (plantry-v0wl) — the latter re-derives the AI-suggested values from the stored line rather than
/// trusting anything the client echoes back.
/// </summary>
public static class ReviewPrefill
{
    /// <summary>The five reference-data lookups <see cref="ComputePrefill"/> needs, built once from the
    /// household's <see cref="ReviewReferenceData"/> and reused across every line.</summary>
    public sealed record Lookups(
        IReadOnlyDictionary<string, Guid> UnitIdByCode,
        IReadOnlyDictionary<Guid, string> ProductNameById,
        IReadOnlyDictionary<Guid, Guid?> ProductDefaultLocationById,
        IReadOnlyDictionary<Guid, Guid> ProductDefaultUnitById,
        IReadOnlyDictionary<Guid, int?> ProductDefaultDueDaysById);

    /// <summary>Builds the <see cref="Lookups"/> from reference data — the single construction point shared
    /// by the hydration builder and the commit auto-confirm pass.</summary>
    public static Lookups BuildLookups(ReviewReferenceData reference) =>
        new(
            reference.Units.ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase),
            reference.Products.ToDictionary(p => p.Id, p => p.Name),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultLocationId),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultUnitId),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultDueDays));

    /// <summary>Convenience overload over a prebuilt <see cref="Lookups"/> — the shape both non-test callers use.</summary>
    public static (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, Guid? LocationId, decimal? Price, DateOnly? Expiry) ComputePrefill(
        ReviewLineView line, Lookups lookups, DateOnly today) =>
        ComputePrefill(line, lookups.UnitIdByCode, lookups.ProductNameById, lookups.ProductDefaultLocationById,
            lookups.ProductDefaultUnitById, lookups.ProductDefaultDueDaysById, today);

    /// <summary>
    /// Pure prefill computation — no URL or HTTP context needed. Applies the priority chain:
    /// user-resolved fields first, AI suggestions for Pending lines as fallback. Only uses
    /// <paramref name="unitIdByCode"/> (label → Guid) and <paramref name="productNameById"/>
    /// (Guid → name).
    ///
    /// <para>Unit priority: user-resolved > receipt-parsed label > (no-receipt-unit only) product default.
    /// Expiry: user-resolved > (Pending + matched + DefaultDueDays) today+N > null.</para>
    /// </summary>
    public static (Guid? ProductId, string? ProductName, decimal? Qty, Guid? UnitId, Guid? LocationId, decimal? Price, DateOnly? Expiry) ComputePrefill(
        ReviewLineView line,
        IReadOnlyDictionary<string, Guid> unitIdByCode,
        IReadOnlyDictionary<Guid, string> productNameById,
        IReadOnlyDictionary<Guid, Guid?> productDefaultLocationById,
        IReadOnlyDictionary<Guid, Guid>? productDefaultUnitById = null,
        IReadOnlyDictionary<Guid, int?>? productDefaultDueDaysById = null,
        DateOnly? today = null)
    {
        var isPending = line.Status == LineStatus.Pending;

        Guid? prefillProductId = line.IsNewProduct ? null
            : line.ProductId
              ?? (isPending && line.SuggestedProductId is { } sugPid && productNameById.ContainsKey(sugPid)
                  ? sugPid : (Guid?)null);

        string? prefillProductName = line.IsNewProduct ? null
            : prefillProductId is { } ppid && productNameById.TryGetValue(ppid, out var ppname)
                ? ppname
                : (isPending ? line.SuggestedProductName : null);

        // Weight→each high-confidence override (plantry-1mu): for a pending, not-yet-resolved line whose
        // matched product is tracked by each, a High-confidence LLM estimate pre-fills the each-count in
        // the each unit (the product's default). Low-confidence estimates fall through to the weight below
        // — the drawer merely suggests the count. The receipt weight is preserved separately regardless.
        var eachOverride = isPending
            && line.UnitId is null
            && line.EstimatedEachCount is { } && line.EstimatedEachConfidence == SuggestedConfidence.High
            && prefillProductId is { } eachPid
            && productDefaultUnitById is not null
            && productDefaultUnitById.TryGetValue(eachPid, out var eachUnitId)
            ? (Guid?)eachUnitId
            : null;

        var prefillQty = line.Quantity
            ?? (eachOverride is not null ? line.EstimatedEachCount : null)
            ?? (isPending ? line.SuggestedQuantity : null);

        var hasReceiptUnit = isPending && line.SuggestedUnitLabel is not null;

        Guid? prefillUnitId = line.UnitId
            ?? eachOverride
            ?? (isPending && line.SuggestedUnitLabel is { } lbl && unitIdByCode.TryGetValue(lbl, out var sugUid)
                ? sugUid
                : (isPending && !hasReceiptUnit && prefillProductId is { } unitPid
                   && productDefaultUnitById is not null
                   && productDefaultUnitById.TryGetValue(unitPid, out var defUid)
                    ? defUid
                    : (Guid?)null));

        Guid? prefillLocationId = line.LocationId
            ?? (isPending && prefillProductId is { } locPid && productDefaultLocationById.TryGetValue(locPid, out var defLoc)
                ? defLoc
                : (Guid?)null);

        var prefillPrice = line.Price ?? (isPending ? line.SuggestedPrice : null);

        DateOnly? prefillExpiry = line.ExpiryDate
            ?? (isPending && prefillProductId is { } expPid
                && productDefaultDueDaysById is not null
                && productDefaultDueDaysById.TryGetValue(expPid, out var dueDays)
                && dueDays is { } n
                && today is { } t
                ? t.AddDays(n)
                : (DateOnly?)null);

        return (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice, prefillExpiry);
    }
}
