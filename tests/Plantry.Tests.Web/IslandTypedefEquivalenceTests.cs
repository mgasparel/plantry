using System.Reflection;
using System.Text.Json.Serialization;
using Plantry.Web.Pages.Intake;
using Plantry.Web.Pages.MealPlan;
using Plantry.Web.Pages.Pantry.TakeStock;

namespace Plantry.Tests.Web;

/// <summary>
/// Reflection-based typedef↔DTO equivalence tests (plantry-eoj5 Phase C).
///
/// Principle (ADR-020 §6 breadcrumb): agents ARE the code generators — the durable
/// asset is the CHECK that tells them when the hand-written JSDoc @typedef has drifted
/// from the C# hydration DTO, not an automated generator. This test enforces that.
///
/// For each hydration DTO type it computes the expected wire key set:
///   • [JsonPropertyName("…")] if present (Take Stock records use these).
///   • camelCase(PropertyName) otherwise (Intake / Meal Planner use the CamelCase policy).
/// Then it parses the matching @typedef block from the island JS file and asserts the
/// name-sets are equal. Failure messages name the diff (missing / extra keys) so the
/// next agent can fix it mechanically.
///
/// Scope: server→island hydration typedefs. Each is driven from the C# DTO side — a type
/// with no mapping entry here is out of scope. The Meal Planner editor payload (EditorState /
/// DishDraft) IS covered now that it has named DTOs (MealEditorHydrationVm / EditorDishHydrationVm).
/// Genuinely island-internal types (LineState with Signal fields, Row, CellMutationResult,
/// SearchResultDish) are not — they never cross the wire.
/// </summary>
public sealed class IslandTypedefEquivalenceTests
{
    // ─── Paths ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the path to a wwwroot JS island file relative to this assembly.
    /// The test assembly sits at tests/Plantry.Tests.Web/bin/…; the islands live at
    /// src/Plantry.Web/wwwroot/js/islands/.
    /// </summary>
    private static string IslandPath(string filename)
    {
        // Walk up from bin/Debug/net10.0 → tests/Plantry.Tests.Web → tests → repo root
        // then descend into src/Plantry.Web/wwwroot/js/islands/.
        var binDir = Path.GetDirectoryName(typeof(IslandTypedefEquivalenceTests).Assembly.Location)!;
        var repoRoot = FindRepoRoot(binDir)
            ?? throw new InvalidOperationException($"Could not locate repo root from {binDir}");
        var path = Path.Combine(repoRoot, "src", "Plantry.Web", "wwwroot", "js", "islands", filename);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Island JS not found at {path}", path);
        return path;
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Plantry.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ─── Reflection helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the wire-format JSON key for each public instance property on the type,
    /// honouring [JsonPropertyName] when present, and applying camelCase otherwise
    /// (matching the CamelCase serializer policy used by Intake/MealPlan/TakeStock pages).
    /// </summary>
    private static IReadOnlySet<string> GetDtoWireKeys(Type dtoType)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var key = attr is not null ? attr.Name : ToCamelCase(prop.Name);
            keys.Add(key);
        }
        return keys;
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    // ─── JSDoc parser ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>@property {type} name</c> lines from a named <c>@typedef {Object} TypedefName</c>
    /// block in the given JS source. Returns the property name set.
    ///
    /// Throws if the typedef cannot be found — a missing typedef is itself a drift signal
    /// (someone renamed it without updating the mapping), not a quiet pass.
    /// </summary>
    private static IReadOnlySet<string> ParseTypedefKeys(string js, string typedefName)
    {
        // Match: @typedef {Object} TypedefName  (possibly with trailing whitespace/comments)
        // We need to find the block start and then collect @property lines until the next
        // @typedef, end of block comment, or blank line pattern that ends the block.
        var lines = js.Split('\n');

        // Find the line index of the typedef declaration.
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            // Match "@typedef {Object} TypedefName" (word boundary after name)
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    lines[i],
                    @"@typedef\s+\{Object\}\s+" + System.Text.RegularExpressions.Regex.Escape(typedefName) + @"\b"))
            {
                start = i;
                break;
            }
        }

        if (start == -1)
            throw new InvalidOperationException(
                $"@typedef {{Object}} {typedefName} not found in JS source. " +
                $"If the name changed, update the DTO↔typedef name mapping in {nameof(IslandTypedefEquivalenceTests)}.");

        // Collect @property lines from the block. The block ends at the next @typedef
        // declaration OR at */  (end of JSDoc comment block) — whichever comes first.
        //
        // The type annotation may contain nested braces (e.g. {Array<{...}>}), so we
        // parse the property name by skipping the balanced-brace type token rather than
        // using a simple [^}]+ pattern.

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];

            // Another @typedef declaration ends this block.
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"@typedef"))
                break;

            // The closing */ of a JSDoc block comment ends it.
            if (line.TrimStart().StartsWith("*/", StringComparison.Ordinal))
                break;

            // Find "@property " and then skip the balanced-brace type token to reach the name.
            var propIdx = line.IndexOf("@property", StringComparison.Ordinal);
            if (propIdx < 0) continue;

            // Skip past "@property " whitespace to find the opening "{".
            var pos = propIdx + "@property".Length;
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            if (pos >= line.Length || line[pos] != '{') continue;

            // Skip the balanced-brace type token (handles nested {}).
            int depth = 0;
            while (pos < line.Length)
            {
                if (line[pos] == '{') depth++;
                else if (line[pos] == '}') { depth--; if (depth == 0) { pos++; break; } }
                pos++;
            }

            // Skip whitespace, then read the identifier (may be preceded by "[" for optional).
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            if (pos < line.Length && line[pos] == '[') pos++; // optional marker
            var nameStart = pos;
            while (pos < line.Length && (char.IsLetterOrDigit(line[pos]) || line[pos] == '_')) pos++;
            if (pos > nameStart)
                keys.Add(line[nameStart..pos]);
        }

        return keys;
    }

    /// <summary>
    /// Parses a COMPACT single-line typedef of the form:
    /// <c>/** @typedef {{ key: type, key2: type2 }} TypedefName */</c>
    /// and returns the key name set.
    /// </summary>
    private static IReadOnlySet<string> ParseCompactTypedefKeys(string js, string typedefName)
    {
        // Match: @typedef {{ ... }} TypedefName
        var pattern = new System.Text.RegularExpressions.Regex(
            @"@typedef\s+\{\{([^}]+)\}\}\s+" + System.Text.RegularExpressions.Regex.Escape(typedefName) + @"\b");

        var m = pattern.Match(js);
        if (!m.Success)
            throw new InvalidOperationException(
                $"Compact @typedef {{{{ ... }}}} {typedefName} not found in JS source. " +
                $"If the name changed, update the DTO↔typedef name mapping in {nameof(IslandTypedefEquivalenceTests)}.");

        // Extract individual "key: type" pairs from the inner body.
        var body = m.Groups[1].Value; // e.g. " unitId: string, code: string "
        var keyPattern = new System.Text.RegularExpressions.Regex(@"(\w+)\s*:");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match km in keyPattern.Matches(body))
            keys.Add(km.Groups[1].Value);

        return keys;
    }

    /// <summary>
    /// Parses the inline object type declared as the element of a named array property in a
    /// @typedef block. Used for <c>SessionHydration.lines</c> which is declared as:
    /// <c>@property {Array&lt;{line: LineSeed, prefill: PrefillData, alternatives: ...}&gt;} lines</c>.
    /// Returns the key set of the inline object (the "wrapper" shape).
    /// </summary>
    private static IReadOnlySet<string> ParseInlineArrayElementKeys(
        string js, string typedefName, string propertyName)
    {
        // Find the @property line for the given property name inside the given typedef block.
        var lines = js.Split('\n');
        int blockStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    lines[i],
                    @"@typedef\s+\{Object\}\s+" + System.Text.RegularExpressions.Regex.Escape(typedefName) + @"\b"))
            {
                blockStart = i;
                break;
            }
        }
        if (blockStart == -1)
            throw new InvalidOperationException(
                $"@typedef {{Object}} {typedefName} not found when looking for inline element type of {propertyName}.");

        // Find the @property {Array<{...}>} <propertyName> line in the block.
        var propLinePattern = new System.Text.RegularExpressions.Regex(
            @"@property\s+\{Array\s*<\s*\{([^}]+)\}\s*[>\]|]+\}\s+" +
            System.Text.RegularExpressions.Regex.Escape(propertyName) + @"\b");

        for (int i = blockStart + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"@typedef"))
                break;
            if (line.TrimStart().StartsWith("*/", StringComparison.Ordinal))
                break;

            var m = propLinePattern.Match(line);
            if (m.Success)
            {
                // m.Groups[1] is the inner object body: "line: LineSeed, prefill: PrefillData, ..."
                var body = m.Groups[1].Value;
                var keyPattern = new System.Text.RegularExpressions.Regex(@"(\w+)\s*:");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (System.Text.RegularExpressions.Match km in keyPattern.Matches(body))
                    keys.Add(km.Groups[1].Value);
                return keys;
            }
        }

        throw new InvalidOperationException(
            $"@property [...Array<{{...}}>] {propertyName} not found in @typedef {typedefName} block.");
    }

    // ─── Assertion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that the wire key set from a C# DTO exactly matches the property name set
    /// from a JS @typedef block. Failure message names the diff so an agent can fix it
    /// mechanically without opening the files.
    /// </summary>
    private static void AssertEquivalent(
        string dtoLabel,
        IReadOnlySet<string> dtoKeys,
        IReadOnlySet<string> jsKeys)
    {
        var missing = dtoKeys.Except(jsKeys).OrderBy(k => k).ToList();
        var extra   = jsKeys.Except(dtoKeys).OrderBy(k => k).ToList();

        if (missing.Count == 0 && extra.Count == 0) return;

        var missingStr = missing.Count > 0 ? string.Join(", ", missing) : "(none)";
        var extraStr   = extra.Count   > 0 ? string.Join(", ", extra)   : "(none)";

        Assert.Fail(
            $"typedef {dtoLabel}: " +
            $"missing [{missingStr}], " +
            $"extra [{extraStr}]. " +
            $"DTO keys: [{string.Join(", ", dtoKeys.OrderBy(k => k))}]; " +
            $"JS typedef keys: [{string.Join(", ", jsKeys.OrderBy(k => k))}].");
    }

    // ─── Tests: Intake (intake-review.js) ────────────────────────────────────

    [Fact]
    public void Intake_SessionHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "SessionHydration",
            GetDtoWireKeys(typeof(SessionHydration)),
            ParseTypedefKeys(js, "SessionHydration"));
    }

    [Fact]
    public void Intake_ProductHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "ProductHydration",
            GetDtoWireKeys(typeof(ProductHydration)),
            ParseTypedefKeys(js, "ProductHydration"));
    }

    [Fact]
    public void Intake_ProductDefaults_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "ProductDefaults",
            GetDtoWireKeys(typeof(ProductDefaults)),
            ParseTypedefKeys(js, "ProductDefaults"));
    }

    [Fact]
    public void Intake_SkuOption_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "SkuOption",
            GetDtoWireKeys(typeof(SkuOption)),
            ParseTypedefKeys(js, "SkuOption"));
    }

    [Fact]
    public void Intake_UnitHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "UnitHydration",
            GetDtoWireKeys(typeof(UnitHydration)),
            ParseTypedefKeys(js, "UnitHydration"));
    }

    [Fact]
    public void Intake_LocationHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "LocationHydration",
            GetDtoWireKeys(typeof(LocationHydration)),
            ParseTypedefKeys(js, "LocationHydration"));
    }

    [Fact]
    public void Intake_CategoryHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "CategoryHydration",
            GetDtoWireKeys(typeof(CategoryHydration)),
            ParseTypedefKeys(js, "CategoryHydration"));
    }

    [Fact]
    public void Intake_AlternativeHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "AlternativeHydration",
            GetDtoWireKeys(typeof(AlternativeHydration)),
            ParseTypedefKeys(js, "AlternativeHydration"));
    }

    /// <summary>
    /// LineSeed equivalence — this is the red→green proof.
    ///
    /// Before the fix: @typedef LineSeed in intake-review.js declares a vestigial
    /// @property {PrefillData} prefill which has no corresponding field on the C# LineSeed
    /// DTO. The wire carries prefill as a sibling of line (in the LineHydration wrapper),
    /// never inside LineSeed. This test flags that as: "typedef LineSeed: missing [], extra [prefill]".
    ///
    /// After the fix (removal of that stale @property line): the test passes green.
    /// </summary>
    [Fact]
    public void Intake_LineSeed_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "LineSeed",
            GetDtoWireKeys(typeof(LineSeed)),
            ParseTypedefKeys(js, "LineSeed"));
    }

    [Fact]
    public void Intake_PrefillData_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        AssertEquivalent(
            "PrefillData",
            GetDtoWireKeys(typeof(PrefillData)),
            ParseTypedefKeys(js, "PrefillData"));
    }

    /// <summary>
    /// LineHydration has no named @typedef in JS — its shape is expressed as an inline
    /// object type in the @property annotation for SessionHydration.lines:
    ///   Array&lt;{line: LineSeed, prefill: PrefillData, alternatives: AlternativeHydration[]|null}&gt;
    /// We verify the inline object's key set matches the C# LineHydration DTO.
    /// </summary>
    [Fact]
    public void Intake_LineHydration_wrapper_keys_match_dto()
    {
        var js = File.ReadAllText(IslandPath("intake-review.js"));
        var jsKeys = ParseInlineArrayElementKeys(js, "SessionHydration", "lines");
        AssertEquivalent(
            "LineHydration (inline wrapper)",
            GetDtoWireKeys(typeof(LineHydration)),
            jsKeys);
    }

    // ─── Tests: Meal Planner (meal-planner.js) ────────────────────────────────

    /// <summary>
    /// IslandHydrationVm (C# name) ↔ IslandHydration (@typedef name in meal-planner.js).
    /// </summary>
    [Fact]
    public void MealPlan_IslandHydrationVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("meal-planner.js"));
        AssertEquivalent(
            "IslandHydration",
            GetDtoWireKeys(typeof(IslandHydrationVm)),
            ParseTypedefKeys(js, "IslandHydration"));
    }

    /// <summary>
    /// IslandMemberVm (C# name) ↔ MemberInfo (@typedef name in meal-planner.js).
    /// </summary>
    [Fact]
    public void MealPlan_IslandMemberVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("meal-planner.js"));
        AssertEquivalent(
            "MemberInfo",
            GetDtoWireKeys(typeof(IslandMemberVm)),
            ParseTypedefKeys(js, "MemberInfo"));
    }

    /// <summary>
    /// MealEditorHydrationVm (C# name, GET ?handler=EditorJson payload) ↔ EditorState (@typedef).
    /// This is the editor seam that previously drifted (typedef was missing the three date fields
    /// and declared a dead cellKey) while the payload was an anonymous object the compiler couldn't
    /// guard. Now named + pinned here.
    /// </summary>
    [Fact]
    public void MealPlan_MealEditorHydrationVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("meal-planner.js"));
        AssertEquivalent(
            "EditorState",
            GetDtoWireKeys(typeof(MealEditorHydrationVm)),
            ParseTypedefKeys(js, "EditorState"));
    }

    /// <summary>
    /// EditorDishHydrationVm (C# name, the dishes[] element of the EditorJson payload) ↔ DishDraft
    /// (@typedef). The island consumes wire dishes straight as draft state, so the shapes must match.
    /// </summary>
    [Fact]
    public void MealPlan_EditorDishHydrationVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("meal-planner.js"));
        AssertEquivalent(
            "DishDraft",
            GetDtoWireKeys(typeof(EditorDishHydrationVm)),
            ParseTypedefKeys(js, "DishDraft"));
    }

    // ─── Tests: Take Stock (take-stock.js) ───────────────────────────────────

    /// <summary>
    /// IslandRowVm (C# name) ↔ RowSeed (@typedef name in take-stock.js).
    /// Take Stock records use [JsonPropertyName] attributes — GetDtoWireKeys honours them.
    /// </summary>
    [Fact]
    public void TakeStock_IslandRowVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("take-stock.js"));
        AssertEquivalent(
            "RowSeed",
            GetDtoWireKeys(typeof(IslandRowVm)),
            ParseTypedefKeys(js, "RowSeed"));
    }

    /// <summary>
    /// UnitOptionVm (C# name) ↔ UnitOption (compact typedef in take-stock.js).
    /// The JS uses the compact single-line form: @typedef {{ unitId: string, code: string }} UnitOption
    /// so we use the compact parser.
    /// </summary>
    [Fact]
    public void TakeStock_UnitOptionVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("take-stock.js"));
        AssertEquivalent(
            "UnitOption",
            GetDtoWireKeys(typeof(UnitOptionVm)),
            ParseCompactTypedefKeys(js, "UnitOption"));
    }

    // ─── Tests: Deals judgement-call deck (deal-deck.js) ─────────────────────

    /// <summary>
    /// DealDeckCardVm (C# name) ↔ DealDeckCard (@typedef in deal-deck.js). The fourth island
    /// (ADR-020 2026-07-07 amendment) hydrates its one-card-at-a-time deck from this shape.
    /// </summary>
    [Fact]
    public void Deals_DealDeckCardVm_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("deal-deck.js"));
        AssertEquivalent(
            "DealDeckCard",
            GetDtoWireKeys(typeof(Plantry.Web.Pages.Deals.DealDeckCardVm)),
            ParseTypedefKeys(js, "DealDeckCard"));
    }

    /// <summary>
    /// DealDeckHydration (C# name, the step-2 hydration payload) ↔ DealDeckConfig (@typedef).
    /// </summary>
    [Fact]
    public void Deals_DealDeckHydration_typedef_matches_dto()
    {
        var js = File.ReadAllText(IslandPath("deal-deck.js"));
        AssertEquivalent(
            "DealDeckConfig",
            GetDtoWireKeys(typeof(Plantry.Web.Pages.Deals.DealDeckHydration)),
            ParseTypedefKeys(js, "DealDeckConfig"));
    }
}
