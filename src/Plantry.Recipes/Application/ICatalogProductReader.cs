namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto Catalog (recipes-domain-model.md §8). Gives Author, Cook, and
/// Fulfillment the slice of a product they need — name, <c>track_stock</c>, default unit, and the
/// depth-1 parent/variant tree (DM-10, DM-19) — plus a name search for the ingredient editor. Defined
/// here in Recipes.Application and <b>implemented in Plantry.Web</b> over Catalog's repositories, so the
/// Recipes project keeps its <c>→ SharedKernel only</c> dependency. All identifiers cross as raw
/// <see cref="Guid"/> soft refs (DM-3).
/// </summary>
public interface ICatalogProductReader
{
    /// <summary>Resolves a single product (with its parent/variant tree); null when it does not exist in this household.</summary>
    Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Batch resolution for the select-existing authoring path — the name, <c>track_stock</c>, and
    /// default unit each chosen ingredient row needs, in a single round-trip. Unlike
    /// <see cref="ResolveSummariesAsync"/> it also carries <c>DefaultUnitId</c> (needed for the R7/C10
    /// unit→product-default conversion-path check); like it, it does <b>not</b> load the depth-1
    /// parent/variant tree (the authoring select path never reads it). Ids absent from this household
    /// are omitted, and existence semantics match <see cref="FindAsync"/> (RLS-scoped, archived
    /// products included). Use over a loop of <see cref="FindAsync"/> when resolving a whole ingredient
    /// list, to avoid an N+1 round-trip per row (plantry-xgmb).
    ///
    /// <para>The default implementation falls back to a per-id <see cref="FindAsync"/> loop so test
    /// doubles need not reimplement it; the production adapter overrides it with one batched query.</para>
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, CatalogProductLookup>> FindManyAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, CatalogProductLookup>();
        foreach (var id in productIds.Distinct())
        {
            var product = await FindAsync(id, ct);
            if (product is not null)
                result[id] = new CatalogProductLookup(product.Id, product.Name, product.TrackStock, product.DefaultUnitId);
        }
        return result;
    }

    /// <summary>
    /// Batch analogue of <see cref="FindAsync"/> that DOES load the depth-1 parent/variant tree
    /// (DM-19) — the tree-carrying counterpart to the lookup-only <see cref="FindManyAsync"/>. Resolves
    /// a whole recipe's products (with their live variant children) in a single round-trip so
    /// <c>FulfillmentService</c> can do the DM-19 stock rollup without an N+1 catalog round-trip per
    /// ingredient (plantry-jnhs). Ids absent from this household are omitted; existence and tree
    /// semantics match <see cref="FindAsync"/> (RLS-scoped, archived products included, archived
    /// variants filtered out of the tree).
    ///
    /// <para>The default implementation falls back to a per-id <see cref="FindAsync"/> loop so test
    /// doubles need not reimplement it; the production adapter overrides it with one batched query.</para>
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, CatalogProduct>> FindManyWithVariantsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, CatalogProduct>();
        foreach (var id in productIds.Distinct())
        {
            var product = await FindAsync(id, ct);
            if (product is not null)
                result[id] = product;
        }
        return result;
    }

    /// <summary>
    /// Name search for the ingredient editor — active products whose name contains
    /// <paramref name="nameQuery"/> (case-insensitive). Empty/whitespace query returns no candidates.
    /// </summary>
    Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default);

    /// <summary>
    /// Batch display resolution for a set of products in a single round-trip — the name and
    /// <c>track_stock</c> each ingredient row needs to render. Unlike <see cref="FindAsync"/>, this
    /// does <b>not</b> load the depth-1 parent/variant tree (the read-only render has no use for the
    /// DM-19 rollup); callers that need the tree must use <see cref="FindAsync"/>. Ids absent from
    /// this household are simply omitted from the result. Use over a loop of <see cref="FindAsync"/>
    /// when resolving a whole ingredient list, to avoid an N+1 round-trip per row.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Resolves unit ids to their display code (e.g. "g", "ea") in a single round-trip — for
    /// rendering an ingredient quantity beside its unit. Ids absent from this household are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default);

    /// <summary>
    /// Lists all active (non-archived) units for the household — the ingredient editor needs them
    /// to populate the unit dropdown. Returns id and display code for each unit, ordered by code.
    /// </summary>
    Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists active parent group products (IsParent = true) for the household, ordered by name —
    /// for the create-view Group combobox in the ingredient editor (plantry-orix).
    /// </summary>
    Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists active (non-archived) categories for the household, ordered by name —
    /// for the Defaults collapsible in the create view (plantry-orix).
    /// </summary>
    Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default);
}

/// <summary>The display slice of a Catalog product for a recipe ingredient row (name + stock-tracking).</summary>
public sealed record CatalogProductSummary(Guid Id, string Name, bool TrackStock);

/// <summary>
/// The slice of a Catalog product the select-existing authoring path resolves per chosen ingredient row —
/// name, <c>track_stock</c>, and default unit (for the R7/C10 conversion-path check). Unlike
/// <see cref="CatalogProductSummary"/> it carries <see cref="DefaultUnitId"/>; unlike
/// <see cref="CatalogProduct"/> it omits the parent/variant tree the authoring select path never reads.
/// </summary>
public sealed record CatalogProductLookup(Guid Id, string Name, bool TrackStock, Guid DefaultUnitId);

/// <summary>
/// A unit option for the ingredient editor dropdown. <see cref="Dimension"/> (e.g. "mass", "volume",
/// "count") and <see cref="FactorToBase"/> let the editor build the axis-locked conversion dropdowns
/// (LEFT = stock-dimension units, RIGHT = recipe-line-dimension units) and derive the client-side echo
/// line, without a per-unit round-trip (plantry-qno9). Both carry backwards-compatible defaults so
/// callers that only need id + code are unaffected.
/// </summary>
public sealed record CatalogUnitOption(Guid Id, string Code, string Dimension = "", decimal FactorToBase = 1m);

/// <summary>
/// The slice of a Catalog product Recipes depends on, including the depth-1 parent/variant tree.
/// <see cref="VariantProductIds"/> holds the live (non-archived) variants when this product is a
/// parent (DM-19 rollup); <see cref="ParentProductId"/> is set when this product is itself a variant.
/// </summary>
public sealed record CatalogProduct(
    Guid Id,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId,
    Guid? ParentProductId,
    bool IsParent,
    IReadOnlyList<Guid> VariantProductIds);

/// <summary>A search hit for the ingredient editor's product picker, with a fuzzy match score.</summary>
public sealed record CatalogProductCandidate(
    Guid Id,
    string Name,
    bool TrackStock,
    Guid DefaultUnitId,
    /// <summary>
    /// Fuzzy match score in [0, 1] from <c>ProductNameMatcher</c>.
    /// Used by the page model to emit ranking labels (<c>.rk</c>) in the search result
    /// <c>&lt;li&gt;</c> markup. Defaults to 1.0 (perfect match) for callers that do not rank.
    /// </summary>
    double Score = 1.0);

/// <summary>A parent product group option for the ingredient editor's Group combobox (plantry-orix).</summary>
public sealed record CatalogGroupOption(Guid Id, string Name);

/// <summary>A product category option for the ingredient editor's Defaults collapsible (plantry-orix).</summary>
public sealed record CatalogCategoryOption(Guid Id, string Name);
