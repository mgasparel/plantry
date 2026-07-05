namespace Plantry.Recipes.Domain;

/// <summary>
/// Lifecycle state of a <see cref="CookConsumeLine"/> (plantry-292b).
/// Persisted as <c>text</c> + <c>CHECK</c> per DataModels/conventions.md, mirroring
/// <see cref="TagCategory"/> and Intake's <c>ImportStatus</c>.
/// <list type="bullet">
/// <item><see cref="Pending"/> — the line was planned before any consume ran; the anchor-first
/// commit records it here before the inventory call is issued.</item>
/// <item><see cref="Applied"/> — <see cref="IInventoryConsumer.ConsumeAsync"/> returned (with or
/// without a partial shortfall); a journal row exists in Inventory.</item>
/// <item><see cref="Shorted"/> — the product had no stock record at all
/// (<see cref="InvalidOperationException"/> from the consumer), or the shortfall equalled the full
/// requested quantity. No journal row was written. Reconciliation (292c) does NOT re-drive Shorted
/// lines — without new stock being added, a re-drive would produce the same outcome. Re-drive
/// Pending lines only.</item>
/// <item><see cref="DeferredUnitGap"/> (plantry-qll2.6) — the consume could not run because no
/// <c>ProductConversion</c> bridged the ingredient unit to the product's stock unit
/// (<c>Catalog.UnresolvableConversion</c>). This is NOT a shortfall: the pantry was untouched (the
/// consume planning pass fails atomically before any lot mutation), and the consume is owed once the
/// math arrives. It is retro-applied automatically when a conversion for that (product, unit-pair)
/// lands, whatever its provenance — see <c>ApplyDeferredUnitGaps</c>. A genuine no-stock
/// <see cref="Shorted"/> line is NEVER retried (that would drive stock negative); the two must stay
/// distinct.</item>
/// <item><see cref="SupersededByCount"/> (plantry-qll2.6) — a terminal state for a
/// <see cref="DeferredUnitGap"/> line voided because an absolute observation (Take Stock count /
/// manual absolute adjustment) on the product captured reality directly. Deferred consume is a
/// relative delta; an absolute observation supersedes it, so retro-applying afterwards would
/// double-count. Voided lines are never retro-applied.</item>
/// </list>
/// </summary>
public enum CookConsumeLineStatus { Pending, Applied, Shorted, DeferredUnitGap, SupersededByCount }

public static class CookConsumeLineStatusExtensions
{
    public static string ToDbValue(this CookConsumeLineStatus status) => status switch
    {
        CookConsumeLineStatus.Pending => "Pending",
        CookConsumeLineStatus.Applied => "Applied",
        CookConsumeLineStatus.Shorted => "Shorted",
        CookConsumeLineStatus.DeferredUnitGap => "DeferredUnitGap",
        CookConsumeLineStatus.SupersededByCount => "SupersededByCount",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static CookConsumeLineStatus Parse(string value) => value switch
    {
        "Pending" => CookConsumeLineStatus.Pending,
        "Applied" => CookConsumeLineStatus.Applied,
        "Shorted" => CookConsumeLineStatus.Shorted,
        "DeferredUnitGap" => CookConsumeLineStatus.DeferredUnitGap,
        "SupersededByCount" => CookConsumeLineStatus.SupersededByCount,
        _ => throw new ArgumentException($"Unknown cook consume line status '{value}'.", nameof(value)),
    };
}
