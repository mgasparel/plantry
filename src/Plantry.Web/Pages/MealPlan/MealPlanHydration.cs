using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plantry.Web.Pages.MealPlan;

// Named DTOs for the Meal Planner island's hydration payload (plantry-eoj5, Phase A).
//
// These replace the anonymous-object serializer options formerly assembled inline in
// Index.cshtml.cs::BuildIslandHydrationJson, giving the server↔island contract a
// single named definition that the compiler keeps refactor-safe and that the island's
// JSDoc @typedef block (meal-planner.js) mirrors 1:1.
//
// Wire format is unchanged: serialized with PropertyNamingPolicy = CamelCase, so
// PascalCase members emit camelCase keys, and property declaration order matches the
// original records byte-for-byte. The consumer-contract test (Phase B) locks
// this shape. UI/transport contract only — no domain logic lives here (ADR-020 §2/§7).

/// <summary>Top-level hydration blob emitted into the Meal Planner page's
/// island data script tag.</summary>
public sealed record IslandHydrationVm(
    string AssignUrl,
    string ClearUrl,
    string RollupUrl,
    string EditorJsonUrl,
    string SearchJsonUrl,
    IReadOnlyList<IslandMemberVm> Members);

/// <summary>Household member info for the island attendee toggle.</summary>
public sealed record IslandMemberVm(
    string UserId,
    string DisplayName,
    string Initials,
    int ColorIndex);

/// <summary>The exact serializer options the Meal Planner page emits hydration with. Shared so the
/// consumer-contract test (plantry-eoj5 Phase B) pins the same camelCase / always-emit policy
/// the island parses against — the only thing spanning the otherwise compiler-less seam.</summary>
public static class MealPlanHydrationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
