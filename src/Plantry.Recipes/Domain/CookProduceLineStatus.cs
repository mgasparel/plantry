namespace Plantry.Recipes.Domain;

/// <summary>
/// Lifecycle state of a <see cref="CookProduceLine"/> — the yield-on-cook inventory ADD (plantry-854a).
/// Mirrors <see cref="CookConsumeLineStatus"/>: persisted as <c>text</c> + <c>CHECK</c> per
/// DataModels/conventions.md.
/// <list type="bullet">
/// <item><see cref="Pending"/> — planned before the inventory add ran; the anchor-first commit records
/// it here before <c>IInventoryProducer.ProduceAsync</c> is issued.</item>
/// <item><see cref="Applied"/> — the produce landed: a positive journal row / lot exists in Inventory.
/// Idempotent re-drives (reconciliation) short-circuit at the Inventory layer via the line's
/// <c>sourceLineRef</c> token, so re-applying never double-adds.</item>
/// <item><see cref="Failed"/> — the add could not be recorded (e.g. the yield product does not exist or
/// cannot hold stock). Terminal — reconciliation (plantry-292c) re-drives <see cref="Pending"/> lines
/// only; a Failed produce would fail again without the product being fixed first.</item>
/// </list>
/// </summary>
public enum CookProduceLineStatus { Pending, Applied, Failed }

public static class CookProduceLineStatusExtensions
{
    public static string ToDbValue(this CookProduceLineStatus status) => status switch
    {
        CookProduceLineStatus.Pending => "Pending",
        CookProduceLineStatus.Applied => "Applied",
        CookProduceLineStatus.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static CookProduceLineStatus Parse(string value) => value switch
    {
        "Pending" => CookProduceLineStatus.Pending,
        "Applied" => CookProduceLineStatus.Applied,
        "Failed" => CookProduceLineStatus.Failed,
        _ => throw new ArgumentException($"Unknown cook produce line status '{value}'.", nameof(value)),
    };
}
