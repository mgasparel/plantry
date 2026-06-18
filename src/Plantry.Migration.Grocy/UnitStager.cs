using Plantry.Migration.Grocy.Dto;

namespace Plantry.Migration.Grocy;

/// <summary>
/// Stages all Grocy quantity units from the manifest into <see cref="UnitStagingRow"/> records.
///
/// Algorithm (per grocy-import-plan.md §4.1):
/// 1. Seed-match by name/synonym: Grocy unit names are compared (case-insensitive, trimmed)
///    against a known synonym table that maps to seeded Plantry unit codes + dimensions + factors.
///    Seed matches inherit the Plantry factor (not Grocy's stored factor) — anomalies are flagged.
/// 2. For non-seed-matched units: build the global conversion graph from the manifest's
///    QuantityUnitConversions where product_id IS NULL (22 global edges). Find connected
///    components via BFS/union-find. A component containing a known mass node ⇒ mass;
///    containing ml ⇒ volume; else ⇒ count. Walk graph edges to derive factor_to_base
///    relative to the component's base unit.
/// 3. Isolated units (no global conversion edges) ⇒ count, factor 1.
///
/// Anomalies pre-flagged:
/// - tsp: Grocy factor ≈ 14.79 (anomalous — close to tbsp); Plantry seed wins (4.92892).
/// - tbsp: Grocy factor ≈ 17.76 (anomalous — off by ~20%); Plantry seed wins (14.7868).
/// - Cup: Grocy factor ≈ 237 ml; Plantry seed wins (240 ml, +1.3% drift).
/// - 1/2 Cup, 1/4 Cup: redundant fractions — Skipped with a note.
/// </summary>
public static class UnitStager
{
    // ──────────── Seed synonym table ──────────────────────────────────────
    // Maps Grocy unit name (lower-case, trimmed) → (Plantry code, dimension, factor_to_base, anomaly note?)
    // These units match an existing seeded Plantry unit. Plantry's factor always wins.
    // Synonyms cover alternate Grocy spellings (e.g. "liter" / "litre" / "l").

    private sealed record SeedEntry(
        string PlantryCode,
        string PlantryName,
        string Dimension,
        decimal PlantryFactor,
        string? AnomalyNote = null);

    private static readonly Dictionary<string, SeedEntry> SeedSynonyms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Mass
            ["gram"]     = new("g",   "Gram",       "mass",   1m),
            ["grams"]    = new("g",   "Gram",       "mass",   1m),
            ["g"]        = new("g",   "Gram",       "mass",   1m),
            ["kg"]       = new("kg",  "Kilogram",   "mass",   1000m),
            ["kilogram"] = new("kg",  "Kilogram",   "mass",   1000m),
            ["kilograms"]= new("kg",  "Kilogram",   "mass",   1000m),
            ["oz"]       = new("oz",  "Ounce",      "mass",   28.3495m),
            ["ounce"]    = new("oz",  "Ounce",      "mass",   28.3495m),
            ["ounces"]   = new("oz",  "Ounce",      "mass",   28.3495m),

            // Volume
            ["ml"]         = new("ml",  "Milliliter", "volume", 1m),
            ["milliliter"] = new("ml",  "Milliliter", "volume", 1m),
            ["millilitre"] = new("ml",  "Milliliter", "volume", 1m),
            ["liter"]      = new("l",   "Liter",      "volume", 1000m),
            ["litre"]      = new("l",   "Liter",      "volume", 1000m),
            ["l"]          = new("l",   "Liter",      "volume", 1000m),
            ["cup"]        = new("cup", "Cup",        "volume", 240m,
                "Grocy stored 237 ml; using Plantry's 240 ml (+1.3% drift). You may override to 237 to preserve exact recipe math."),
            ["tsp"]          = new("tsp",  "Teaspoon",   "volume", 4.92892m,
                "ANOMALY: Grocy stored 14.7867 ml (≈ a tablespoon). Using Plantry's 4.92892 ml. Override to 14.7867 only if your recipe data was entered assuming this wrong value."),
            ["teaspoon"]     = new("tsp",  "Teaspoon",   "volume", 4.92892m,
                "ANOMALY: Grocy stored 14.7867 ml (≈ a tablespoon). Using Plantry's 4.92892 ml. Override to 14.7867 only if your recipe data was entered assuming this wrong value."),
            ["tbsp"]         = new("tbsp", "Tablespoon", "volume", 14.7868m,
                "ANOMALY: Grocy stored 17.7581 ml (off by ~20%). Using Plantry's 14.7868 ml. Override to 17.7581 only if your recipe data was entered assuming this wrong value."),
            ["tablespoon"]   = new("tbsp", "Tablespoon", "volume", 14.7868m,
                "ANOMALY: Grocy stored 17.7581 ml (off by ~20%). Using Plantry's 14.7868 ml. Override to 17.7581 only if your recipe data was entered assuming this wrong value."),

            // Count
            ["piece"]    = new("ea", "Each",   "count", 1m),
            ["pieces"]   = new("ea", "Each",   "count", 1m),
            ["ea"]       = new("ea", "Each",   "count", 1m),
            ["each"]     = new("ea", "Each",   "count", 1m),
            ["pack"]     = new("pk", "Pack",   "count", 1m),
            ["packs"]    = new("pk", "Pack",   "count", 1m),
            ["pk"]       = new("pk", "Pack",   "count", 1m),
            ["package"]  = new("pk", "Pack",   "count", 1m),
            ["dozen"]    = new("doz", "Dozen", "count", 12m),
            ["doz"]      = new("doz", "Dozen", "count", 12m),
        };

    // Units to be explicitly skipped (redundant fractions — collapsed to base × fraction).
    private static readonly HashSet<string> SkipNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "1/2 cup", "half cup",
            "1/4 cup", "quarter cup",
        };

    // For graph-based dimension assignment, we need to know which Grocy unit names correspond
    // to known dimensional anchors. We'll look these up after building the staging rows.
    // Key = Grocy unit name (lower), Value = (dimension, factor_to_base anchor).
    private static readonly Dictionary<string, (string Dimension, decimal Factor)> DimensionAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Mass anchors (relative to gram = base, factor 1)
            ["gram"]  = ("mass", 1m),
            ["grams"] = ("mass", 1m),
            ["g"]     = ("mass", 1m),
            // Volume anchors (relative to ml = base, factor 1)
            ["ml"]         = ("volume", 1m),
            ["milliliter"] = ("volume", 1m),
            ["millilitre"] = ("volume", 1m),
        };

    // Known "create new" units with pre-defined codes, names, dimensions, and factors.
    // These are volume/count units NOT in the seed set. Keyed by Grocy unit name (lower-case).
    private static readonly Dictionary<string, SeedEntry> KnownCreateEntries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pint"]    = new("pt",     "Pint",    "volume", 474m),
            ["quart"]   = new("qt",     "Quart",   "volume", 948m),
            ["case12"]  = new("case12", "Case 12", "count",  12m),
            ["case24"]  = new("case24", "Case 24", "count",  24m),
        };

    /// <summary>
    /// Stages all units from the manifest and returns the staging rows in Grocy-id order.
    /// </summary>
    public static IReadOnlyList<UnitStagingRow> Stage(GrocyManifest manifest)
    {
        var units = manifest.QuantityUnits;
        // Global conversions only (no product-specific overrides)
        var globalConversions = manifest.QuantityUnitConversions
            .Where(c => c.ProductId is null)
            .ToList();

        // Build a lookup from Grocy unit id → name for graph traversal
        var idToName = units.ToDictionary(u => u.Id, u => u.Name);

        // ── Step 1: Seed-match + known-skip pass ──────────────────────────
        var rows = new List<UnitStagingRow>(units.Count);
        var seedMatchedIds = new HashSet<int>(); // Grocy ids resolved in step 1
        var skipIds = new HashSet<int>();

        foreach (var unit in units)
        {
            var nameTrimmed = unit.Name.Trim();

            // Explicit skip (redundant fractions)?
            if (SkipNames.Contains(nameTrimmed))
            {
                rows.Add(new UnitStagingRow
                {
                    GrocyId     = unit.Id,
                    GrocyName   = unit.Name,
                    Dimension   = "count",
                    PlantryCode = null,
                    PlantryName = null,
                    FactorToBase= 1m,
                    Action      = UnitMappingAction.MatchExisting,
                    Status      = UnitStagingStatus.Skipped,
                    AnomalyNote = $"Redundant fraction — skipping. Use 'cup' × the appropriate fraction instead.",
                });
                seedMatchedIds.Add(unit.Id);
                skipIds.Add(unit.Id);
                continue;
            }

            // Seed match?
            if (SeedSynonyms.TryGetValue(nameTrimmed, out var seed))
            {
                rows.Add(new UnitStagingRow
                {
                    GrocyId     = unit.Id,
                    GrocyName   = unit.Name,
                    Dimension   = seed.Dimension,
                    PlantryCode = seed.PlantryCode,
                    PlantryName = seed.PlantryName,
                    FactorToBase= seed.PlantryFactor,
                    Action      = UnitMappingAction.MatchExisting,
                    Status      = seed.AnomalyNote is not null
                                    ? UnitStagingStatus.NeedsReview
                                    : UnitStagingStatus.Auto,
                    AnomalyNote = seed.AnomalyNote,
                });
                seedMatchedIds.Add(unit.Id);
                continue;
            }

            // Known create entry (Pint, Quart, Case12, Case24)?
            if (KnownCreateEntries.TryGetValue(nameTrimmed, out var create))
            {
                rows.Add(new UnitStagingRow
                {
                    GrocyId     = unit.Id,
                    GrocyName   = unit.Name,
                    Dimension   = create.Dimension,
                    PlantryCode = create.PlantryCode,
                    PlantryName = create.PlantryName,
                    FactorToBase= create.PlantryFactor,
                    Action      = UnitMappingAction.CreateNew,
                    Status      = UnitStagingStatus.Auto,
                    AnomalyNote = null,
                });
                seedMatchedIds.Add(unit.Id);
                continue;
            }

            // Placeholder for graph pass
            rows.Add(new UnitStagingRow
            {
                GrocyId  = unit.Id,
                GrocyName= unit.Name,
            });
        }

        // ── Step 2: Global conversion graph for unresolved units ──────────

        // Build adjacency list: node = Grocy unit id, edge = (toId, factor from→to)
        var adj = new Dictionary<int, List<(int ToId, decimal Factor)>>();
        foreach (var unit in units)
        {
            adj[unit.Id] = [];
        }
        foreach (var conv in globalConversions)
        {
            if (!adj.ContainsKey(conv.FromQuId) || !adj.ContainsKey(conv.ToQuId))
                continue;
            adj[conv.FromQuId].Add((conv.ToQuId, conv.Factor));
            // Add reverse edge: if 1 from = F to, then 1 to = 1/F from
            adj[conv.ToQuId].Add((conv.FromQuId, 1m / conv.Factor));
        }

        // BFS to find connected components and resolve dimension + factor_to_base
        var visited = new HashSet<int>(seedMatchedIds); // seed-matched ids don't need graph resolution
        var unresolvedRows = rows.Where(r => !seedMatchedIds.Contains(r.GrocyId)).ToList();
        var unresolvedIds  = unresolvedRows.Select(r => r.GrocyId).ToHashSet();

        // For each unresolved unit, run BFS from that unit to find its component
        // and check if any node in the component is a known dimensional anchor.
        var resolvedByGraph = new Dictionary<int, (string Dimension, decimal FactorToBase)>();

        foreach (var startId in unresolvedIds)
        {
            if (resolvedByGraph.ContainsKey(startId))
                continue;

            // BFS: collect component + path factors from startId
            var component = new List<int>();
            var factorFromStart = new Dictionary<int, decimal> { [startId] = 1m };
            var queue = new Queue<int>();
            queue.Enqueue(startId);
            var bfsVisited = new HashSet<int> { startId };

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                component.Add(cur);

                foreach (var (toId, factor) in adj.GetValueOrDefault(cur, []))
                {
                    if (bfsVisited.Contains(toId))
                        continue;
                    bfsVisited.Add(toId);
                    // factorFromStart[toId] = factorFromStart[cur] × factor
                    // meaning: 1 startId = factorFromStart[cur] × factor × toId units
                    // Wait, we need to be careful about direction:
                    //   edge (cur → toId) with edge-factor F means: 1 cur = F toId
                    //   so 1 start = factorFromStart[cur] toId (along path)
                    //   => 1 start = factorFromStart[cur] × F toId units
                    // No wait: adj edge (cur → toId, factor F) means "1 cur = F toId units"
                    // factorFromStart[cur] means "1 start = factorFromStart[cur] cur"
                    // => 1 start = factorFromStart[cur] × F toId
                    factorFromStart[toId] = factorFromStart[cur] * factor;
                    queue.Enqueue(toId);
                }
            }

            // Does this component contain any seeded units (which have known dimensions)?
            // Check by id: seeded ids are in seedMatchedIds. Find the seeded row for each.
            string? componentDimension = null;
            decimal? baseFactorOfAnchor = null; // factor from start to the anchor
            int? anchorId = null;

            foreach (var nodeId in component.Concat(bfsVisited))
            {
                if (!idToName.TryGetValue(nodeId, out var nodeName))
                    continue;
                // Check if this node matches a known dimensional anchor name
                if (DimensionAnchors.TryGetValue(nodeName.Trim(), out var anchor))
                {
                    componentDimension = anchor.Dimension;
                    baseFactorOfAnchor = factorFromStart.TryGetValue(nodeId, out var f) ? f : 1m;
                    anchorId = nodeId;
                    break;
                }
                // Also check if this node is a seed-matched unit we can borrow dimension from
                if (seedMatchedIds.Contains(nodeId))
                {
                    var existingRow = rows.FirstOrDefault(r => r.GrocyId == nodeId);
                    if (existingRow?.Status != UnitStagingStatus.Skipped && existingRow?.Dimension is not null)
                    {
                        componentDimension ??= existingRow.Dimension;
                        // Can't easily derive factor_to_base from a non-anchor seed; will handle below
                    }
                }
            }

            // Determine factor_to_base for each unresolved unit in this component.
            // factor_to_base = factor from *this unit* to the base unit of the dimension.
            // We know: 1 start = factorFromStart[node] node units
            // So: 1 node = 1/factorFromStart[node] start units
            // And: 1 start to anchor = factorFromStart[anchorId] anchor-factor ...
            // Actually: 1 node × factor_to_base(node) = 1 base_unit
            // We want: factor_to_base(node) = ?
            //
            // If anchor is the base (factor_to_base = 1), and we know:
            //   1 start = factorFromStart[node] node
            //   1 start = factorFromStart[anchor] anchor (where anchor is base, factor 1)
            // => 1 node = factorFromStart[anchor] / factorFromStart[node] anchor
            // => factor_to_base(node) = factorFromStart[anchor] / factorFromStart[node]

            foreach (var nodeId in component)
            {
                if (!unresolvedIds.Contains(nodeId))
                    continue;

                decimal ftb;
                if (componentDimension is null || anchorId is null || baseFactorOfAnchor is null)
                {
                    // No dimensional anchor found — default to count, factor 1
                    ftb = 1m;
                    componentDimension = "count";
                }
                else
                {
                    var factorNodeFromStart = factorFromStart.TryGetValue(nodeId, out var fn) ? fn : 1m;
                    var factorAnchorFromStart = factorFromStart.TryGetValue(anchorId.Value, out var fa) ? fa : 1m;
                    // 1 node = (factorAnchorFromStart / factorNodeFromStart) anchor
                    ftb = factorAnchorFromStart / factorNodeFromStart;
                }

                resolvedByGraph[nodeId] = (componentDimension!, ftb);
            }
        }

        // Apply graph resolution to placeholder rows
        foreach (var row in rows)
        {
            if (seedMatchedIds.Contains(row.GrocyId))
                continue;

            if (resolvedByGraph.TryGetValue(row.GrocyId, out var resolved))
            {
                row.Dimension   = resolved.Dimension;
                row.FactorToBase= Math.Round(resolved.FactorToBase, 6);
            }
            else
            {
                // Isolated unit (no global conversions) — count, factor 1
                row.Dimension   = "count";
                row.FactorToBase= 1m;
            }

            // Derive a Plantry code and name from the Grocy name for create-new units.
            row.PlantryCode = DeriveCode(row.GrocyName);
            row.PlantryName = row.GrocyName.Trim();
            row.Action      = UnitMappingAction.CreateNew;
            row.Status      = UnitStagingStatus.Auto;
            row.AnomalyNote = null;
        }

        return rows.OrderBy(r => r.GrocyId).ToList();
    }

    /// <summary>
    /// Derives a short Plantry unit code from a Grocy unit name:
    /// lower-case, strip non-alphanumeric, max 20 chars.
    /// </summary>
    private static string DeriveCode(string grocyName)
    {
        var raw = grocyName.Trim().ToLowerInvariant();
        // Keep letters, digits, underscores
        var clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (clean.Length == 0) clean = "unit";
        return clean.Length <= 20 ? clean : clean[..20];
    }
}
