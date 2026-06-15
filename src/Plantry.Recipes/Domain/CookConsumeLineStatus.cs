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
/// requested quantity. No journal row was written; reconciliation (292c) may re-drive this line.</item>
/// </list>
/// </summary>
public enum CookConsumeLineStatus { Pending, Applied, Shorted }

public static class CookConsumeLineStatusExtensions
{
    public static string ToDbValue(this CookConsumeLineStatus status) => status switch
    {
        CookConsumeLineStatus.Pending => "Pending",
        CookConsumeLineStatus.Applied => "Applied",
        CookConsumeLineStatus.Shorted => "Shorted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static CookConsumeLineStatus Parse(string value) => value switch
    {
        "Pending" => CookConsumeLineStatus.Pending,
        "Applied" => CookConsumeLineStatus.Applied,
        "Shorted" => CookConsumeLineStatus.Shorted,
        _ => throw new ArgumentException($"Unknown cook consume line status '{value}'.", nameof(value)),
    };
}
