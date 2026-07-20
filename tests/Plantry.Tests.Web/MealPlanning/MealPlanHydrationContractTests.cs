using System.Text.Json;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Pages.MealPlan;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// Consumer-contract test for the Meal Planner island's hydration payload (plantry-eoj5 Phase B).
///
/// The server emits this shape (Index.cshtml.cs::BuildIslandHydrationJson → the named DTOs in
/// MealPlanHydration.cs) and the island parses it by hand against a JSDoc @typedef block
/// (meal-planner.js). No compiler spans that seam. This test pins the EXACT camelCase key set at
/// every nesting level, so dropping or renaming a field the island reads fails here — loudly,
/// server-side — instead of surfacing as <c>undefined</c> in the browser.
///
/// Cross-checked against the <c>@typedef IslandHydration</c> and <c>@typedef MemberInfo</c>
/// blocks in <c>meal-planner.js</c>.
/// Serialization uses the same <see cref="MealPlanHydrationJson.Options"/> the page emits with.
/// </summary>
public sealed class MealPlanHydrationContractTests
{
    private static JsonElement Serialize(IslandHydrationVm h) =>
        JsonDocument.Parse(JsonSerializer.Serialize(h, MealPlanHydrationJson.Options)).RootElement;

    /// <summary>A fully-populated payload — every nested shape present so the key assertions
    /// cover the whole contract surface cross-checked against meal-planner.js @typedef blocks.</summary>
    private static IslandHydrationVm Sample() => new(
        AssignUrl: "/MealPlan?handler=AssignJson",
        ClearUrl: "/MealPlan?handler=ClearJson",
        RollupUrl: "/MealPlan?handler=RollupJson",
        EditorJsonUrl: "/MealPlan?handler=EditorJson",
        SearchJsonUrl: "/MealPlan?handler=SearchJson",
        CurrencySymbol: "$",
        Members:
        [
            new IslandMemberVm(
                UserId: "00000000-0000-0000-0000-000000000001",
                DisplayName: "Alice Smith",
                Initials: "AS",
                ColorIndex: 0),
        ]);

    [Fact]
    public void Root_has_exact_island_key_set()
    {
        // Cross-checked against @typedef IslandHydration in meal-planner.js:
        //   assignUrl, clearUrl, rollupUrl, editorJsonUrl, searchJsonUrl, currencySymbol, members
        HydrationContract.AssertKeys(Serialize(Sample()),
            "assignUrl", "clearUrl", "rollupUrl", "editorJsonUrl", "searchJsonUrl", "currencySymbol", "members");
    }

    [Fact]
    public void Member_has_exact_key_set()
    {
        // Cross-checked against @typedef MemberInfo in meal-planner.js:
        //   userId, displayName, initials, colorIndex
        var member = Serialize(Sample()).GetProperty("members")[0];
        HydrationContract.AssertKeys(member, "userId", "displayName", "initials", "colorIndex");
    }

    /// <summary>
    /// Spot-mutation proof: renaming a record property would break the island seam.
    /// This test shows what would fail if, for example, <c>DisplayName</c> were renamed to
    /// <c>Name</c> — the island's <c>member.displayName</c> read would get <c>undefined</c>.
    /// The test is structural (it mutates the expected key set); rename <c>DisplayName</c> in
    /// <see cref="IslandMemberVm"/> and confirm this test goes red, then revert.
    /// </summary>
    [Fact]
    public void Spot_mutation_proof_wrong_key_is_detected()
    {
        var member = Serialize(Sample()).GetProperty("members")[0];
        // "name" does NOT exist in the serialized output (the property is "displayName").
        // Asserting it is present must fail — confirming drift is caught here, not silently
        // becoming `undefined` in the browser.
        Assert.False(member.TryGetProperty("name", out _),
            "Serialized member must NOT have a 'name' key — it would mean DisplayName was renamed without updating the island typedef.");
    }
}
