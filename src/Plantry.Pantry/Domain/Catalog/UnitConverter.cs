using Plantry.SharedKernel;

namespace Plantry.Pantry.Domain;

/// <summary>
/// Resolves a quantity from one unit to another (DM-12). Pure and side-effect free — callers
/// supply the household's units and the product's conversions; nothing is loaded here.
///
/// Units are treated as nodes in a small graph and resolution walks it (BFS — these graphs are
/// small, so shortest-path-by-hop-count is both cheap and the natural "most direct" answer),
/// composing factors along whichever edges connect <c>fromUnitId</c> to <c>toUnitId</c>:
///   1. Identity: the same unit, factor 1 — always an edge, checked first as a short-circuit.
///   2. Same-dimension linear scaling via <see cref="Unit.FactorToBase"/> — but ONLY for a
///      genuine physical dimension (<see cref="Dimension.Mass"/> / <see cref="Dimension.Volume"/>).
///      <see cref="Dimension.Count"/> is a bucket for otherwise-unrelated counting units (e.g. a
///      "serving" and a "pack" on the same product) that share no universal ratio — two distinct
///      Count units are connected only if a <see cref="ProductConversion"/> says so.
///   3. Product-specific <see cref="ProductConversion"/> edges, traversable in both directions
///      (forward multiplies by the stored factor, reverse divides by it — never a precomputed
///      reciprocal, to avoid baking in decimal-division rounding before it's needed).
///
/// Composing edges (2) and (3) transitively lets the walk chain arbitrarily many hops through a
/// shared pivot unit — e.g. resolving "srv" against "pk" via "srv → cup → g → pk" when only
/// cup/g and pk/g conversions are configured, not a direct srv/pk one. When no path exists, the
/// walk fails loudly — it never falls back to an identity or zero result.
///
/// Unit IDs are raw <see cref="Guid"/>s (not the strongly-typed <see cref="UnitId"/>) so this
/// service can be driven directly from cross-context values such as <see cref="Quantity"/>.
/// </summary>
public static class UnitConverter
{
    /// <summary>
    /// Structural shape of the three <see cref="Unit"/> fields the algorithm actually reads. Lets a
    /// caller that cannot legitimately materialize a <see cref="Unit"/> aggregate (e.g.
    /// <c>WeekBagEnricher</c>, driven off a flat ADR-021 SQL projection rather than an EF load) still
    /// run the exact same conversion graph (plantry-jvd7).
    /// </summary>
    public readonly record struct UnitShape(Guid Id, Dimension Dimension, decimal FactorToBase);

    /// <summary>Structural shape of the three <see cref="ProductConversion"/> fields the algorithm actually reads — see <see cref="UnitShape"/>.</summary>
    public readonly record struct ConversionShape(Guid FromUnitId, Guid ToUnitId, decimal Factor);

    /// <summary>
    /// Entity-typed overload — a thin wrapper that maps <see cref="Unit"/>/<see cref="ProductConversion"/>
    /// aggregates to their structural shapes and delegates to the shape-typed overload below. Kept
    /// exactly as-is (same signature) so every existing call site is unaffected (plantry-jvd7): the
    /// Catalog domain surface stays additive, not breaking.
    /// </summary>
    public static Result<decimal> Convert(
        decimal amount,
        Guid fromUnitId,
        Guid toUnitId,
        IReadOnlyCollection<Unit> units,
        IReadOnlyCollection<ProductConversion> productConversions) =>
        Convert(
            amount, fromUnitId, toUnitId,
            units.Select(u => new UnitShape(u.Id.Value, u.Dimension, u.FactorToBase)).ToList(),
            productConversions.Select(c => new ConversionShape(c.FromUnitId.Value, c.ToUnitId.Value, c.Factor)).ToList());

    /// <summary>
    /// Shape-typed overload — the canonical algorithm. Structural inputs let a read-model caller drive
    /// the same BFS without materializing EF aggregates (plantry-jvd7); the entity-typed overload above
    /// is a thin wrapper over this one.
    /// </summary>
    public static Result<decimal> Convert(
        decimal amount,
        Guid fromUnitId,
        Guid toUnitId,
        IReadOnlyCollection<UnitShape> units,
        IReadOnlyCollection<ConversionShape> conversions)
    {
        if (fromUnitId == toUnitId)
            return amount;

        var graph = BuildConversionGraph(units, conversions);

        // BFS: shortest hop count wins, and among same-length options the earliest-discovered
        // edge wins (mirrors the old resolution-order contract — same-dimension edges are added
        // before product-conversion edges, and conversions are walked in list order — see
        // DirectProductConversion_PreferredOver_Inverse_When_Both_Match).
        //
        // The running value carried through the queue is the ACTUAL amount-so-far, not an
        // abstract multiplier composed separately and applied to `amount` at the end — a reverse
        // ProductConversion edge divides by its stored factor directly (amount / factor), exactly
        // as the old single-hop code did, rather than pre-computing and multiplying by 1/factor.
        // Decimal division doesn't always terminate cleanly (e.g. 1/600 rounds to ~29 significant
        // digits before it's ever used), so composing that way could leave a sub-epsilon residue
        // even when the true quotient (e.g. 78.75/600 = 0.13125) is exact.
        var visited = new HashSet<Guid> { fromUnitId };
        var queue = new Queue<(Guid UnitId, decimal Value)>();
        queue.Enqueue((fromUnitId, amount));

        while (queue.Count > 0)
        {
            var (currentId, currentValue) = queue.Dequeue();

            if (!graph.TryGetValue(currentId, out var edges))
                continue;

            foreach (var edge in edges)
            {
                if (!visited.Add(edge.To))
                    continue;

                var nextValue = edge.Invert ? currentValue / edge.Factor : currentValue * edge.Factor;

                if (edge.To == toUnitId)
                    return nextValue;

                queue.Enqueue((edge.To, nextValue));
            }
        }

        return Error.Custom("Catalog.UnresolvableConversion",
            $"No conversion is known from unit '{fromUnitId}' to unit '{toUnitId}'.");
    }

    /// <summary>An edge in the conversion graph: multiply by <see cref="Factor"/>, or divide by it when <see cref="Invert"/>.</summary>
    private readonly record struct ConversionEdge(Guid To, decimal Factor, bool Invert);

    /// <summary>
    /// Builds the conversion graph's adjacency list: same-dimension scale edges for genuine
    /// physical dimensions, then <see cref="ProductConversion"/> edges (both directions).
    /// </summary>
    private static Dictionary<Guid, List<ConversionEdge>> BuildConversionGraph(
        IReadOnlyCollection<UnitShape> units,
        IReadOnlyCollection<ConversionShape> conversions)
    {
        var graph = new Dictionary<Guid, List<ConversionEdge>>();

        void AddEdge(Guid from, Guid to, decimal factor, bool invert)
        {
            if (!graph.TryGetValue(from, out var edges))
                graph[from] = edges = [];
            edges.Add(new ConversionEdge(to, factor, invert));
        }

        // Same-dimension linear-scale edges — Mass and Volume only. Count is excluded: it is a
        // catch-all for counting units with no inherent shared ratio (a "serving" is not
        // universally some multiple of a "pack"), so two distinct Count units connect only
        // through an explicit ProductConversion below, never for free.
        var scalable = units.Where(u => u.Dimension is Dimension.Mass or Dimension.Volume).ToList();
        foreach (var from in scalable)
        {
            foreach (var to in scalable)
            {
                if (from.Id == to.Id || from.Dimension != to.Dimension)
                    continue;

                // Guard non-positive FactorToBase (plantry-jvd7): Unit.Create enforces factorToBase >
                // 0, so the entity-typed overload can never supply one — this is a no-op there. A
                // UnitShape built from a flat read-model row (WeekBagEnricher) is not covered by that
                // aggregate invariant, so skip creating a scale edge rather than dividing by (or by
                // way of) a non-positive factor.
                if (from.FactorToBase <= 0 || to.FactorToBase <= 0)
                    continue;

                AddEdge(from.Id, to.Id, from.FactorToBase / to.FactorToBase, invert: false);
            }
        }

        // Product-specific conversions, traversable in both directions: stored direction
        // multiplies by the stored factor, reverse divides by it (never a precomputed reciprocal
        // — see the precision note in Convert above).
        foreach (var conversion in conversions)
        {
            AddEdge(conversion.FromUnitId, conversion.ToUnitId, conversion.Factor, invert: false);
            AddEdge(conversion.ToUnitId, conversion.FromUnitId, conversion.Factor, invert: true);
        }

        return graph;
    }

    /// <summary>
    /// Enumerates every unit the caller can express a quantity in for the given product, ordered
    /// with <paramref name="defaultUnitId"/> first and then alphabetically by code.
    ///
    /// A unit is "reachable" if:
    ///   (a) it is the default unit, OR
    ///   (b) it shares the same <see cref="Unit.Dimension"/> as the default unit (same-dimension
    ///       siblings — no <see cref="ProductConversion"/> required), OR
    ///   (c) it is the <c>FromUnit</c> or <c>ToUnit</c> of any <see cref="ProductConversion"/>
    ///       on the product, OR
    ///   (d) it shares the same <see cref="Unit.Dimension"/> as any such conversion anchor
    ///       (bridged siblings — reached via a same-dimension hop on either side).
    ///
    /// A product with no conversions returns a single-element list (its default unit only).
    /// </summary>
    public static IReadOnlyList<Guid> ReachableUnits(
        Guid defaultUnitId,
        IReadOnlyList<Unit> allUnits,
        IReadOnlyList<ProductConversion> productConversions)
    {
        // Index units for fast lookup.
        var unitById = allUnits.ToDictionary(u => u.Id.Value);

        // Collect the dimensions that are reachable.
        var reachableDimensions = new HashSet<Dimension>();

        // (a)+(b) default unit and its dimension.
        if (unitById.TryGetValue(defaultUnitId, out var defaultUnit))
            reachableDimensions.Add(defaultUnit.Dimension);

        // (c)+(d) each conversion anchor and its dimension siblings.
        foreach (var conv in productConversions)
        {
            if (unitById.TryGetValue(conv.FromUnitId.Value, out var fromUnit))
                reachableDimensions.Add(fromUnit.Dimension);
            if (unitById.TryGetValue(conv.ToUnitId.Value, out var toUnit))
                reachableDimensions.Add(toUnit.Dimension);
        }

        // Every unit whose dimension is reachable is reachable.
        var reachable = allUnits
            .Where(u => reachableDimensions.Contains(u.Dimension))
            .Select(u => u.Id.Value)
            .ToHashSet();

        // Always include the default unit (even if it somehow has no dimension entry).
        reachable.Add(defaultUnitId);

        // Order: default first, then by code ascending.
        var ordered = reachable
            .OrderBy(id => id == defaultUnitId ? 0 : 1)
            .ThenBy(id => unitById.TryGetValue(id, out var u) ? u.Code : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered;
    }
}
