using System.Text.Json;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Shared assertion for the island hydration consumer-contract tests (plantry-eoj5 Phase B).
/// One contract test per island surface (Intake / Meal Planner / Take Stock) pins its payload's
/// exact camelCase key set at every nesting level; this is the assertion common to all three,
/// extracted once the third copy landed (per the "extract before you repeat" rule).
/// </summary>
public static class HydrationContract
{
    /// <summary>Asserts an object's property-name set is EXACTLY <paramref name="expected"/> — catches
    /// both a dropped field (island reads <c>undefined</c>) and an unexpected extra (typedef gone stale).
    /// Deterministic because the payloads serialize with DefaultIgnoreCondition.Never (all keys emitted).</summary>
    public static void AssertKeys(JsonElement obj, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        var actual = obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }
}
