using System.Text.Json;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Pages.Pantry.TakeStock;

namespace Plantry.Tests.Web.TakeStock;

/// <summary>
/// Consumer-contract test for the Take Stock walk island's hydration payload (plantry-eoj5 Phase B).
///
/// The server emits this shape (Walk.cshtml.cs::BuildIslandRowsJson → the named DTOs in
/// TakeStockHydration.cs) and the island parses it by hand against a JSDoc @typedef block
/// (take-stock.js). No compiler spans that seam. This test pins the EXACT camelCase key set at
/// every nesting level, so dropping or renaming a field the island reads fails here — loudly,
/// server-side — instead of surfacing as <c>undefined</c> in the browser.
///
/// Cross-checked against the <c>@typedef RowSeed</c> and
/// <c>@typedef {{ unitId: string, code: string }} UnitOption</c> blocks in <c>take-stock.js</c>.
/// Serialization uses the same <see cref="TakeStockHydrationJson.Options"/> the Walk page emits with.
/// </summary>
public sealed class TakeStockHydrationContractTests
{
    private static JsonElement SerializeRow(IslandRowVm row) =>
        JsonDocument.Parse(JsonSerializer.Serialize(row, TakeStockHydrationJson.Options)).RootElement;

    /// <summary>A fully-populated row — supportedUnits present with one entry — so the key assertions
    /// cover the whole contract surface cross-checked against take-stock.js @typedef blocks.</summary>
    private static IslandRowVm Sample() => new(
        ProductId:      Guid.Parse("22222222-0000-0000-0000-200000000001"),
        ProductName:    "Flour",
        Recorded:       500m,
        UnitCode:       "g",
        UnitId:         Guid.Parse("33333333-0000-0000-0000-300000000001"),
        HasActiveStock: true,
        LotsUrl:        "/pantry/take-stock/11111111-0000-0000-0000-100000000001?handler=Lots&productId=22222222-0000-0000-0000-200000000001",
        SupportedUnits:
        [
            new UnitOptionVm(
                UnitId: Guid.Parse("33333333-0000-0000-0000-300000000001"),
                Code:   "g"),
        ]);

    [Fact]
    public void Row_has_exact_RowSeed_key_set()
    {
        // Cross-checked against @typedef RowSeed in take-stock.js:
        //   productId, productName, recorded, unitCode, unitId, hasActiveStock, lotsUrl, supportedUnits
        HydrationContract.AssertKeys(SerializeRow(Sample()),
            "productId", "productName", "recorded", "unitCode", "unitId",
            "hasActiveStock", "lotsUrl", "supportedUnits");
    }

    [Fact]
    public void UnitOption_has_exact_key_set()
    {
        // Cross-checked against @typedef {{ unitId: string, code: string }} UnitOption in take-stock.js
        var unit = SerializeRow(Sample()).GetProperty("supportedUnits")[0];
        HydrationContract.AssertKeys(unit, "unitId", "code");
    }

    /// <summary>
    /// Spot-mutation proof: renaming a record property would break the island seam.
    /// This test shows what would fail if, for example, <c>ProductName</c> were renamed to
    /// <c>Name</c> — the island's <c>seed.productName</c> read would get <c>undefined</c>.
    /// Rename <c>ProductName</c> in <see cref="IslandRowVm"/> and confirm this test goes red,
    /// then revert.
    /// </summary>
    [Fact]
    public void Spot_mutation_proof_wrong_key_is_detected()
    {
        var row = SerializeRow(Sample());
        // "name" does NOT exist in the serialized output (the property is "productName").
        // Asserting it is present must fail — confirming drift is caught here, not silently
        // becoming `undefined` in the browser.
        Assert.False(row.TryGetProperty("name", out _),
            "Serialized row must NOT have a 'name' key — it would mean ProductName was renamed without updating the island typedef.");
    }
}
