using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plantry.Web.Pages.Pantry.TakeStock;

// Named DTOs for the Take Stock walk island's hydration payload (plantry-eoj5, Phase A).
//
// These replace the private anonymous-type records formerly inlined in
// Walk.cshtml.cs::BuildIslandRowsJson, giving the server↔island contract a single
// named definition that the compiler keeps refactor-safe and that the island's
// JSDoc @typedef block (take-stock.js) mirrors 1:1.
//
// Wire format is unchanged: serialized with PropertyNamingPolicy = CamelCase, so
// PascalCase members emit camelCase keys, and property declaration order matches the
// original records byte-for-byte. The consumer-contract test (Phase B) locks
// this shape. UI/transport contract only — no domain logic lives here (ADR-020 §2/§7).

/// <summary>
/// Per-product row emitted into the Walk page's
/// <c>&lt;script type="application/json" id="ts-walk-data"&gt;</c> array.
/// The Preact island (<c>take-stock.js</c>) hydrates one <c>RowSeed</c> per entry
/// and renders the full count row (product name, recorded quantity, supported units,
/// lot escape-hatch URL).
/// Cross-checked against <c>@typedef RowSeed</c> in <c>take-stock.js</c>.
/// </summary>
public sealed record IslandRowVm(
    [property: JsonPropertyName("productId")]      Guid                       ProductId,
    [property: JsonPropertyName("productName")]    string                     ProductName,
    [property: JsonPropertyName("recorded")]       decimal                    Recorded,
    [property: JsonPropertyName("unitCode")]       string                     UnitCode,
    [property: JsonPropertyName("unitId")]         Guid                       UnitId,
    [property: JsonPropertyName("hasActiveStock")] bool                       HasActiveStock,
    [property: JsonPropertyName("lotsUrl")]        string                     LotsUrl,
    [property: JsonPropertyName("supportedUnits")] List<UnitOptionVm>         SupportedUnits);

/// <summary>
/// A unit the island may present in the per-row unit selector (multi-unit products).
/// Cross-checked against <c>@typedef {{ unitId: string, code: string }} UnitOption</c>
/// in <c>take-stock.js</c>.
/// </summary>
public sealed record UnitOptionVm(
    [property: JsonPropertyName("unitId")] Guid   UnitId,
    [property: JsonPropertyName("code")]   string Code);

/// <summary>The exact serializer options the Walk page emits island hydration with. Shared so the
/// consumer-contract test (plantry-eoj5 Phase B) pins the same camelCase / always-emit policy
/// the island parses against — the only thing spanning the otherwise compiler-less seam.</summary>
public static class TakeStockHydrationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
