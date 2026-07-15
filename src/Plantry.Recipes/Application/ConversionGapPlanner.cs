namespace Plantry.Recipes.Application;

/// <summary>
/// The R7/C10 unit-conversion phase lifted out of <see cref="AuthorRecipe"/> (plantry-xgmb). For the
/// resolved tracked lines it, in order:
/// <list type="number">
///   <item>writes any author-supplied conversion factor to Catalog first, so a just-defined path resolves
///     on this same pass (honouring the explicit four-field unit pair when present, plantry-qno9, else the
///     legacy recipeUnit→productDefault assumption);</item>
///   <item>collects the tracked lines that still have no unit→product-default path;</item>
///   <item>decides whether the save is <see cref="ConversionOutcome.Blocked"/> (default, R7/C10 — the
///     editor must prompt for the factor inline) or the gaps are carried out as
///     <see cref="ConversionOutcome.Ready"/> deferred (edit-moment AI seeding opted in via
///     <c>DeferMissingConversions</c>, plantry-qll2.4 / ADR-022).</item>
/// </list>
/// Stays inside the Recipes-owned Catalog anti-corruption boundary — it talks to Catalog only through the
/// same ports <see cref="AuthorRecipe"/> does.
/// </summary>
public sealed class ConversionGapPlanner(IUnitConverter unitConverter, ICatalogWriter catalogWriter)
{
    /// <summary>
    /// Applies author-supplied factors, collects the remaining cross-dimension gaps, and returns whether
    /// the save is blocked or may proceed (with the gaps deferred when <paramref name="deferMissing"/>).
    /// </summary>
    public async Task<ConversionOutcome> PlanAsync(
        IReadOnlyList<ResolvedLine> resolved, bool deferMissing, CancellationToken ct = default)
    {
        // Apply any author-supplied factors first so a just-written conversion resolves on this same pass.
        foreach (var r in resolved)
        {
            if (NeedsConversionCheck(r) && r.Line.ConversionFactor is { } factor && factor > 0)
            {
                // plantry-qno9: honour an explicit from/to (the four-field "1 kg = 8 cups" equation) verbatim
                // when present; otherwise fall back to the legacy single-factor assumption (from = the recipe
                // line unit, to = product default) used by the post-save row-level backstop.
                var fromUnitId = r.Line.ConversionFromUnitId ?? r.Line.UnitId!.Value;
                var toUnitId = r.Line.ConversionToUnitId ?? r.DefaultUnitId;
                if (fromUnitId != toUnitId)
                    await catalogWriter.AddConversionAsync(r.ProductId, fromUnitId, toUnitId, factor, ct);
            }
        }

        // A missing conversion is a cross-dimension unit gap (same-dimension pairs resolve via the universal
        // factor_to_base and never reach here).
        var missing = new List<ConversionNeeded>();
        foreach (var r in resolved)
        {
            if (!NeedsConversionCheck(r))
                continue;
            var path = await unitConverter.ConvertAsync(r.ProductId, 1m, r.Line.UnitId!.Value, r.DefaultUnitId, ct);
            if (path.IsFailure)
                missing.Add(new ConversionNeeded(r.Line.Ordinal, r.ProductId, r.Line.UnitId!.Value, r.DefaultUnitId));
        }

        // By default the save is blocked so the editor can prompt for the factor inline (R7/C10). When the
        // caller opts into deferral the recipe instead saves WITH the gaps, carried out for async AI seeding.
        if (missing.Count > 0 && !deferMissing)
            return new ConversionOutcome.Blocked(missing);

        return new ConversionOutcome.Ready(deferMissing ? missing : []);
    }

    /// <summary>A tracked line whose unit differs from the product default needs a conversion path (R7).</summary>
    private static bool NeedsConversionCheck(ResolvedLine r) =>
        r.TrackStock && r.Line.UnitId is { } unit && unit != r.DefaultUnitId;
}

/// <summary>
/// The outcome of <see cref="ConversionGapPlanner.PlanAsync"/>. <see cref="Blocked"/> when one or more
/// tracked lines lack a conversion path and deferral is off (save blocked, R7/C10); <see cref="Ready"/>
/// otherwise, carrying the deferred gaps (empty on the normal path, populated when deferral saved with the
/// gaps for async AI seeding).
/// </summary>
public abstract record ConversionOutcome
{
    private ConversionOutcome() { }

    /// <summary>Save is blocked — these tracked lines have no unit→product-default path (R7/C10).</summary>
    public sealed record Blocked(IReadOnlyList<ConversionNeeded> Missing) : ConversionOutcome;

    /// <summary>Save may proceed. <see cref="Deferred"/> lists the gaps saved-with for async seeding (else empty).</summary>
    public sealed record Ready(IReadOnlyList<ConversionNeeded> Deferred) : ConversionOutcome;
}
