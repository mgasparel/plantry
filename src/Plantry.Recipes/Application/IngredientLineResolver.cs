using Plantry.SharedKernel;

namespace Plantry.Recipes.Application;

/// <summary>
/// Resolves each authored ingredient line to a typed <see cref="ResolvedLine"/> — the per-line product
/// resolution phase lifted out of <see cref="AuthorRecipe"/> (plantry-xgmb). A line either chooses an
/// existing product (search/select) or requests an inline create; this component owns the branching
/// between the three tracked-create sub-paths (A join-group / B new-group / C standalone, plantry-orix)
/// and the untracked-staple create (C12), plus the inline tracked-create R5 pre-check that guards against
/// minting an orphan product before the recipe save.
///
/// <para>The chosen-product reads are batched into a single round-trip via
/// <see cref="ICatalogProductReader.FindManyAsync"/> (no per-line await for reads); inline creates remain
/// per-line writes by nature. Line order and error precedence are preserved: <see cref="ResolveAsync"/>
/// returns the first failing line's error, exactly as the original inline loop did. It stays inside the
/// Recipes-owned Catalog anti-corruption boundary — it talks to Catalog only through the same ports
/// <see cref="AuthorRecipe"/> does.</para>
/// </summary>
public sealed class IngredientLineResolver(ICatalogProductReader products, ICatalogWriter catalogWriter)
{
    /// <summary>
    /// Resolves every line in order, returning the ordered <see cref="ResolvedLine"/> list or the first
    /// line's typed error. Chosen (select-existing) product reads are batched up front; inline-create
    /// lines write per line.
    /// </summary>
    public async Task<LineResolution> ResolveAsync(
        IReadOnlyList<AuthorIngredientLine> lines, CancellationToken ct = default)
    {
        // Batch-resolve every chosen (select-existing) product id up front — one round-trip instead of an
        // await-per-line FindAsync. Inline-create lines carry no ProductId and are unaffected.
        var chosenIds = lines
            .Where(l => l.ProductId is not null)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToList();
        var chosen = chosenIds.Count > 0
            ? await products.FindManyAsync(chosenIds, ct)
            : EmptyChosen;

        var resolved = new List<ResolvedLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.ProductId is { } chosenId)
            {
                if (!chosen.TryGetValue(chosenId, out var product))
                    return LineResolution.Fail(
                        Error.Custom("Recipes.UnknownProduct", "A chosen ingredient product does not exist."));
                resolved.Add(new ResolvedLine(product.Id, product.Name, product.TrackStock, product.DefaultUnitId, line));
            }
            else if (line.NewIsTracked && !string.IsNullOrWhiteSpace(line.NewStapleName))
            {
                var (trackedLine, trackedError) = await ResolveInlineTrackedAsync(line, ct);
                if (trackedError is { } error)
                    return LineResolution.Fail(error);
                resolved.Add(trackedLine!.Value);
            }
            else if (!string.IsNullOrWhiteSpace(line.NewStapleName))
            {
                // Untracked staple (C12, NewIsTracked = false).
                if (line.NewStapleDefaultUnitId is not { } stapleUnit)
                    return LineResolution.Fail(
                        Error.Custom("Recipes.MissingStapleUnit", "An inline staple needs a default unit."));
                var newId = await catalogWriter.CreateUntrackedStapleAsync(line.NewStapleName.Trim(), stapleUnit, ct);
                // An inline staple is untracked (track_stock = false) by construction (C12).
                resolved.Add(new ResolvedLine(newId, line.NewStapleName.Trim(), TrackStock: false, stapleUnit, line));
            }
            else
            {
                return LineResolution.Fail(
                    Error.Custom("Recipes.LineMissingProduct", "Each ingredient must choose a product or name a new staple."));
            }
        }

        return LineResolution.Ok(resolved);
    }

    /// <summary>
    /// Inline tracked-product create from the create-view (plantry-orix). Three sub-paths, chosen by the
    /// group fields:
    ///   A. Join existing group — <c>NewGroupId</c> non-empty → <see cref="ICatalogWriter.CreateTrackedVariantAsync"/>;
    ///   B. Create new group     — <c>NewGroupName</c> non-empty → <see cref="ICatalogWriter.CreateTrackedGroupedProductAsync"/>;
    ///   C. Standalone tracked   — both group fields empty → <see cref="ICatalogWriter.CreateTrackedProductAsync"/>.
    /// The R5 pre-check runs BEFORE the Catalog write so no orphan product is minted when the create-view
    /// row has no quantity (the user must re-open it in the search view to fill qty) — plantry-orix.
    /// </summary>
    private async Task<(ResolvedLine? Line, Error? Error)> ResolveInlineTrackedAsync(
        AuthorIngredientLine line, CancellationToken ct)
    {
        if (line.NewStapleDefaultUnitId is not { } trackedUnit)
            return (null, Error.Custom("Recipes.MissingStapleUnit", "An inline tracked product needs a default unit."));

        // R5 pre-check: a tracked ingredient must carry both qty and unitId. Validated before the Catalog
        // write so a missing-quantity create-view row cannot leave an orphan product behind (plantry-orix).
        if (line.Quantity is null || line.UnitId is null)
            return (null, Error.Custom(
                "Recipes.TrackedRequiresQuantity",
                $"'{line.NewStapleName!.Trim()}' (line {line.Ordinal + 1}) is tracked and needs a quantity and unit."));

        var trackedName = line.NewStapleName!.Trim();
        Guid newTrackedId;

        if (!string.IsNullOrWhiteSpace(line.NewGroupId) && Guid.TryParse(line.NewGroupId, out var parentGroupId))
        {
            // Path A: create as a variant of an existing group.
            var unitOverride = trackedUnit == Guid.Empty ? (Guid?)null : trackedUnit;
            newTrackedId = await catalogWriter.CreateTrackedVariantAsync(
                parentGroupId, trackedName, unitOverride, line.NewStapleCategoryId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(line.NewGroupName))
        {
            // Path B: create new group + first variant atomically.
            newTrackedId = await catalogWriter.CreateTrackedGroupedProductAsync(
                line.NewGroupName.Trim(), trackedName, trackedUnit, line.NewStapleCategoryId, ct);
        }
        else
        {
            // Path C: standalone tracked product (no group).
            newTrackedId = await catalogWriter.CreateTrackedProductAsync(trackedName, trackedUnit, line.NewStapleCategoryId, ct);
        }

        // A freshly created tracked product has trackStock: true; its default unit is the supplied unit.
        return (new ResolvedLine(newTrackedId, trackedName, TrackStock: true, trackedUnit, line), null);
    }

    private static readonly IReadOnlyDictionary<Guid, CatalogProductLookup> EmptyChosen =
        new Dictionary<Guid, CatalogProductLookup>();
}

/// <summary>
/// The outcome of <see cref="IngredientLineResolver.ResolveAsync"/>: either the ordered resolved lines
/// (<see cref="Lines"/> set, <see cref="Error"/> null) or the first failing line's typed error
/// (<see cref="Error"/> set, <see cref="Lines"/> null).
/// </summary>
public readonly record struct LineResolution(IReadOnlyList<ResolvedLine>? Lines, Error? Error)
{
    public static LineResolution Ok(IReadOnlyList<ResolvedLine> lines) => new(lines, null);
    public static LineResolution Fail(Error error) => new(null, error);
}

/// <summary>
/// A recipe ingredient line resolved to its Catalog product facts (id, display name, <c>track_stock</c>,
/// default unit) paired with the original authored line. The unit of exchange between
/// <see cref="IngredientLineResolver"/>, <see cref="ConversionGapPlanner"/>, and <see cref="AuthorRecipe"/>.
/// </summary>
public readonly record struct ResolvedLine(
    Guid ProductId, string ProductName, bool TrackStock, Guid DefaultUnitId, AuthorIngredientLine Line);
