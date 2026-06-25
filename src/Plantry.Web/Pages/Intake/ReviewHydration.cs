using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plantry.Web.Pages.Intake;

// Named DTOs for the Intake review island's hydration payload (plantry-eoj5, Phase A).
//
// These replace the anonymous objects formerly assembled inline in
// Review.cshtml.cs::BuildHydrationJson, giving the server↔island contract a single
// named definition that the compiler keeps refactor-safe and that the island's
// JSDoc @typedef block (intake-review.js) mirrors 1:1.
//
// Wire format is unchanged: serialized with PropertyNamingPolicy = CamelCase, so
// PascalCase members emit camelCase keys, and property declaration order matches the
// original anonymous objects byte-for-byte. The consumer-contract test (Phase B) locks
// this shape. UI/transport contract only — no domain logic lives here (ADR-020 §2/§7).

/// <summary>Top-level hydration blob emitted into the Review page's
/// <c>&lt;script type="application/json" id="review-island-data"&gt;</c> tag.</summary>
public sealed record SessionHydration(
    string SessionId,
    string MerchantText,
    string SessionDate,
    string Today,
    string CommitUrl,
    string DiscardUrl,
    string SaveLineUrl,
    string DismissLineUrl,
    string RestoreLineUrl,
    IReadOnlyList<ProductHydration> Products,
    IReadOnlyList<UnitHydration> Units,
    IReadOnlyList<LocationHydration> Locations,
    IReadOnlyList<CategoryHydration> Categories,
    IReadOnlyList<LineHydration> Lines);

/// <summary>A catalog product the drawer can resolve a line to, with the defaults the
/// island applies on (re-)selection (ADR-020 §3 case 2 — single-default fill is UI).</summary>
public sealed record ProductHydration(
    string Id,
    string Name,
    IReadOnlyList<SkuOption> Skus,
    ProductDefaults Defaults);

/// <summary>The unit/location/expiry an island fills into empty fields when a product is selected.</summary>
public sealed record ProductDefaults(
    string UnitId,
    string? LocationId,
    string? Expiry);

/// <summary>A purchasable pack-size option for a matched product.</summary>
public sealed record SkuOption(
    string Id,
    string Label);

public sealed record UnitHydration(
    string Id,
    string Code,
    string Name);

public sealed record LocationHydration(
    string Id,
    string Name);

public sealed record CategoryHydration(
    string Id,
    string Name,
    int? Hue);

/// <summary>One review row: the saved/edited line state, the server-computed prefill
/// (ADR-020 §3 case 1 — the priority chain stays server-side), and resolved alternatives.</summary>
public sealed record LineHydration(
    LineSeed Line,
    PrefillData Prefill,
    IReadOnlyList<AlternativeHydration>? Alternatives);

/// <summary>The persisted line state the island hydrates a row from.</summary>
public sealed record LineSeed(
    string LineId,
    string ReceiptText,
    string Confidence,
    string Status,
    string? ProductId,
    string? SkuId,
    decimal? Quantity,
    string? UnitId,
    string? LocationId,
    string? ExpiryDate,
    decimal? Price,
    bool IsNewProduct,
    string? NewProductName,
    string? NewProductCategoryId,
    decimal? SuggestedPrice);

/// <summary>Server-computed prefill values (the priority chain's output) the drawer renders.
/// The island never re-derives the chain — it consumes these (ADR-020 §3 case 1).</summary>
public sealed record PrefillData(
    string? ProductId,
    string? ProductName,
    decimal? Quantity,
    string? UnitId,
    string? LocationId,
    decimal? Price,
    string? Expiry,
    string? SkuId);

/// <summary>A catalog-resolved "did you mean" candidate for the suggestion block.</summary>
public sealed record AlternativeHydration(
    string ProductId,
    string ProductName,
    decimal Confidence);

/// <summary>The exact serializer options the Review page emits hydration with. Shared so the
/// consumer-contract test (plantry-eoj5 Phase B) pins the same camelCase / always-emit policy
/// the island parses against — the only thing spanning the otherwise compiler-less seam.</summary>
public static class IntakeHydrationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
