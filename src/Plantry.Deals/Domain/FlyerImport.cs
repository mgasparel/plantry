using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Deals.Domain;

/// <summary>
/// Aggregate root (§4 / DJ2 / D2 / DL-O2): one pull of one store's flyer — the ACL provenance envelope,
/// the async-worker unit, the raw-payload quarantine, and the dedup anchor (DD5). Holds <b>no</b> typed
/// deal rows (those are separate <see cref="Deal"/> roots). <b>Retained</b> — never deleted (audit).
/// <para>
/// <see cref="RawFlyer"/> is set once at <see cref="Start"/> and is immutable thereafter (DD6): a re-set
/// after the pull is a guarded failure. <see cref="Status"/> is monotonic: <c>Pulling → Parsed</c> or
/// <c>Pulling → Failed</c> (DD12).
/// </para>
/// </summary>
public sealed class FlyerImport : AggregateRoot<FlyerImportId>
{
    public static readonly Error RawFlyerAlreadySet =
        Error.Custom("Deals.FlyerImport.RawFlyerImmutable", "raw_flyer is set once at Start and cannot be overwritten (DD6).");

    public static readonly Error NotPulling =
        Error.Custom("Deals.FlyerImport.NotPulling", "A flyer import can only transition out of Pulling once (DD12).");

    public static readonly Error NotParsed =
        Error.Custom("Deals.FlyerImport.NotParsed", "Only a Parsed import can record a re-pull (DD5/DD13).");

    private FlyerImport() { } // EF

    private FlyerImport(
        FlyerImportId id, HouseholdId householdId, Guid storeId, string flyerExternalId,
        byte[]? contentHash, ValidityWindow window, string rawFlyer, DateTimeOffset now)
        : base(id)
    {
        HouseholdId = householdId;
        StoreId = storeId;
        FlyerExternalId = flyerExternalId;
        ContentHash = contentHash;
        ValidityWindow = window;
        RawFlyer = rawFlyer;
        Status = PullStatus.Pulling;
        PulledAt = now;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public HouseholdId HouseholdId { get; private set; }

    /// <summary>Soft-ref → catalog.store; which store's flyer.</summary>
    public Guid StoreId { get; private set; }

    /// <summary>Flipp's flyer id; with <c>(household, store)</c> the dedup key (DD5).</summary>
    public string FlyerExternalId { get; private set; } = string.Empty;

    /// <summary>sha256 of the raw payload — secondary dedup (DL-O5).</summary>
    public byte[]? ContentHash { get; private set; }

    /// <summary>The flyer's run dates; copied onto each <see cref="Deal"/> (D9).</summary>
    public ValidityWindow ValidityWindow { get; private set; } = null!;

    /// <summary>The full raw pull payload — the ACL quarantine (jsonb). Set once, opaque to the domain (DD6).</summary>
    public string RawFlyer { get; private set; } = string.Empty;

    public PullStatus Status { get; private set; }

    /// <summary>Set when <see cref="Status"/> is <see cref="PullStatus.Failed"/> (Flipp unreachable / parse error).</summary>
    public string? ErrorDetail { get; private set; }

    public DateTimeOffset PulledAt { get; private set; }
    public DateTimeOffset? ParsedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Factory. Opens a pull in <see cref="PullStatus.Pulling"/> with the raw payload quarantined.</summary>
    public static FlyerImport Start(
        HouseholdId householdId, Guid storeId, string flyerExternalId,
        byte[]? contentHash, ValidityWindow window, string rawFlyer, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(flyerExternalId))
            throw new ArgumentException("Flyer external id must not be blank.", nameof(flyerExternalId));
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rawFlyer);

        return new FlyerImport(
            FlyerImportId.New(), householdId, storeId, flyerExternalId,
            contentHash, window, rawFlyer, clock.UtcNow);
    }

    /// <summary>
    /// Transitions <c>Pulling → Parsed</c> after normalization+match finished (DD12) and emits
    /// <see cref="FlyerImportedEvent"/> carrying the point-in-time <paramref name="pendingCount"/>
    /// (DJ2 step 7 / §9) — the number of deals this pull left <see cref="DealStatus.Pending"/>.
    /// </summary>
    public Result MarkParsed(int pendingCount, IClock clock)
    {
        if (Status != PullStatus.Pulling)
            return NotPulling;

        Status = PullStatus.Parsed;
        ParsedAt = clock.UtcNow;
        UpdatedAt = clock.UtcNow;
        RaiseDomainEvent(new FlyerImportedEvent(Id, HouseholdId, StoreId, pendingCount, clock.UtcNow));
        return Result.Success();
    }

    /// <summary>
    /// Records a <b>changed</b> re-pull of an already-<see cref="PullStatus.Parsed"/> import (DD5/DD13):
    /// refreshes the dedup bookkeeping (<see cref="ContentHash"/> so the next byte-identical pull is a
    /// no-op, and <see cref="ValidityWindow"/> if the flyer's run dates moved) and re-emits
    /// <see cref="FlyerImportedEvent"/> with the new <paramref name="pendingCount"/>. <b>Never touches
    /// <see cref="RawFlyer"/></b> (the first pull's provenance is immutable, DD6) or <see cref="Status"/>.
    /// The deal-refresh itself (only still-<see cref="DealStatus.Pending"/> deals; resolved deals frozen)
    /// is the ingestion service's job — this only advances the import's own bookkeeping.
    /// </summary>
    public Result RecordRepull(byte[]? contentHash, ValidityWindow window, int pendingCount, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Status != PullStatus.Parsed)
            return NotParsed;

        ContentHash = contentHash;
        ValidityWindow = window;
        UpdatedAt = clock.UtcNow;
        RaiseDomainEvent(new FlyerImportedEvent(Id, HouseholdId, StoreId, pendingCount, clock.UtcNow));
        return Result.Success();
    }

    /// <summary>Transitions <c>Pulling → Failed</c>, recording the error detail (DD12).</summary>
    public Result MarkFailed(string errorDetail, IClock clock)
    {
        if (Status != PullStatus.Pulling)
            return NotPulling;

        Status = PullStatus.Failed;
        ErrorDetail = errorDetail;
        UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Guards the write-once invariant (DD6). The payload is already set at <see cref="Start"/>; this
    /// exists so a re-set attempt after parse fails loudly rather than silently clobbering provenance.
    /// </summary>
    public Result SetRawFlyer(string rawFlyer)
    {
        _ = rawFlyer;
        return RawFlyerAlreadySet;
    }
}
