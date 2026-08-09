namespace Plantry.Planning.Application;

/// <summary>
/// Anti-corruption read port onto the Catalog context for the MealPlanning context.
/// Checks whether a product referenced as a dish actually exists in this household's catalog.
/// Implemented in Plantry.Web over PantryDbContext.
/// Note: a same-named interface exists in Plantry.Recipes.Application — this is the MealPlanning copy,
/// intentionally separate to avoid introducing a cross-context dependency.
/// </summary>
public interface IMealPlanCatalogProductReader
{
    /// <summary>Returns true when the product exists in this household's catalog (is not archived).</summary>
    Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the product exists (is not archived) AND is a concrete, non-parent product.
    /// A parent (grouping) product has no resolution point for "which variant was consumed" — no
    /// price observation, no stock record, no fulfillment target — so it cannot be planned as a
    /// direct product dish. Used to gate new dish creation; existing planned dishes that already
    /// reference a parent are grandfathered and unaffected by this check.
    /// </summary>
    Task<bool> IsPlannableAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Name search for the product search in the meal editor.
    /// Returns up to <paramref name="maxResults"/> active catalog products whose name contains the
    /// query. Parent (grouping) products are excluded — only their concrete variants are plannable
    /// as direct product dishes; variants and unrelated leaf products are returned as usual.
    /// </summary>
    Task<IReadOnlyList<MealPlanProductReadModel>> SearchAsync(string nameQuery, int maxResults = 20, CancellationToken ct = default);

    /// <summary>
    /// Resolves product names by ID in a single round-trip.
    /// Ids absent from the catalog are simply omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the product's default unit id, or null when the product is unresolvable (plantry-9n7l).
    /// Used by <see cref="Plantry.Planning.Domain.PlanCostingService"/> to convert a price
    /// observation's unit onto the product's default unit before costing a product-dish — a price
    /// observation can be recorded in a unit other than the product's default (e.g. a weight-priced
    /// Intake line records the receipt's resolved weight unit independent of the product's own default,
    /// <c>CommitSessionCommand.cs</c>), so the two must not be assumed equal.
    /// Default implementation returns null (unresolvable) so existing implementers/test doubles compile
    /// unchanged; the production adapter overrides it.
    /// </summary>
    Task<Guid?> FindDefaultUnitIdAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<Guid?>(null);

    /// <summary>
    /// Returns the current default unit and the server-authoritative reachable choices used when
    /// validating a product dish.  This is an additive ACL seam so existing test doubles can keep
    /// implementing the older reader contract; a null result means the caller must fall back to
    /// the default-unit existence check.
    /// </summary>
    Task<MealPlanProductPlanningInfo?> GetPlanningInfoAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult<MealPlanProductPlanningInfo?>(null);

    Task<IReadOnlyDictionary<Guid, MealPlanProductPlanningInfo>> GetPlanningInfoAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, MealPlanProductPlanningInfo>>(
            new Dictionary<Guid, MealPlanProductPlanningInfo>());

    /// <summary>
    /// Resolves each product's default unit CODE (e.g. "ea", "lb") by ID in a single round-trip —
    /// the display label for a product-dish quantity (plantry-ri26: product dishes were always
    /// labelled "servings" regardless of the product's configured unit). Distinct from
    /// <see cref="FindDefaultUnitIdAsync"/>, which returns the unit's ID for cost-conversion, not a
    /// display label. Ids absent from the catalog, or whose default unit cannot be resolved, are
    /// simply omitted from the result; callers should fall back to a neutral placeholder ("?").
    /// Default implementation returns an empty dictionary so existing implementers/test doubles
    /// compile unchanged; the production adapter overrides it.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveDefaultUnitCodesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    /// <summary>
    /// Resolves unit ids to their display code (e.g. "g", "ea") in a single round-trip — for
    /// rendering the meal card's actual-eaten quantity (plantry-vqa7,
    /// <see cref="Plantry.Planning.Application.DishCookStatus.ConsumedUnitId"/>). Distinct from
    /// <see cref="ResolveDefaultUnitCodesAsync"/>, which is keyed by PRODUCT and returns that
    /// product's DEFAULT unit's code — a journal row's unit can differ from it, so that method must
    /// not be reused here. Ids absent from this household are omitted. Mirrors
    /// <c>Plantry.Recipes.Application.ICatalogProductReader.ResolveUnitCodesAsync</c>.
    /// Default implementation returns an empty dictionary so existing implementers/test doubles
    /// compile unchanged; the production adapter overrides it with one batched query (mirrors
    /// <see cref="ResolveDefaultUnitCodesAsync"/>).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>Display facts for a catalog product in the meal editor.</summary>
/// <param name="UnitCode">
/// The product's default unit's display code (e.g. "ea", "lb") — plantry-ri26. Defaults to
/// <see cref="DishDisplayPlaceholders.UnresolvedUnitCode"/> so existing positional construction
/// sites (only the adapter's SearchAsync today) keep compiling; the adapter always supplies a
/// resolved code or the same placeholder.
/// </param>
public sealed record MealPlanProductReadModel(
    Guid ProductId,
    string Name,
    string UnitCode = DishDisplayPlaceholders.UnresolvedUnitCode,
    Guid? DefaultUnitId = null,
    string? Dimension = null,
    IReadOnlyList<MealPlanUnitOption>? UnitOptions = null);

/// <summary>Catalog facts needed to validate and hydrate a product dish without a Catalog dependency.</summary>
public sealed record MealPlanProductPlanningInfo(
    Guid ProductId,
    Guid DefaultUnitId,
    string Dimension,
    IReadOnlyList<MealPlanUnitOption> UnitOptions);

public sealed record MealPlanUnitOption(Guid UnitId, string Code, string Dimension);

/// <summary>
/// Shared placeholder text for an unresolvable product-dish name/unit (plantry-r2yf AC7). Both the
/// Today and MealPlan projections resolve product-dish names/unit codes via a batched
/// <c>GetValueOrDefault(id, &lt;placeholder&gt;)</c> lookup against <see cref="IMealPlanCatalogProductReader"/>'s
/// results — hoisting the literal text here means the two surfaces cannot silently drift into
/// different wording for the same "unresolvable" case.
/// </summary>
public static class DishDisplayPlaceholders
{
    /// <summary>Shown for a product-dish whose product id could not be resolved to a name.</summary>
    public const string UnknownProductName = "Unknown product";

    /// <summary>Shown for a product-dish whose default unit code could not be resolved.</summary>
    public const string UnresolvedUnitCode = "?";
}
