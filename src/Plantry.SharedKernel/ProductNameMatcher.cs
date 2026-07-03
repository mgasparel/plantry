namespace Plantry.SharedKernel;

/// <summary>
/// Deterministic in-process fuzzy ranker for the inline add-product control
/// (<c>_ProductSearchCreateSheet</c>). Operates on <c>(id, name)</c> pairs + a query string;
/// returns scored hits above the display cutoff (≥ 0.70), ordered score-desc then alphabetical.
///
/// <para>Algorithm (per the design — plantry-hl4a):</para>
/// <list type="number">
/// <item>Normalize both sides: lowercase, strip punctuation to spaces, tokenize on whitespace,
///   singularize each token (<c>-ies→y</c>, <c>-es</c>, <c>-s</c>, guarded by min length).</item>
/// <item>Scoring ladder: exact (1.00) → token-set equal (0.95) → all query tokens ⊆ name tokens (0.90)
///   → mean of per-query-token best Jaro-Winkler against name tokens, with substring boost (floor 0.85
///   when normalized name contains normalized query).</item>
/// <item>Display cutoff: score ≥ 0.70. Nothing below is surfaced.</item>
/// <item>Tiers: exact = 1.0, strong ≥ 0.85, near ≥ 0.70.</item>
/// </list>
///
/// <para>Jaro-Winkler is chosen over Levenshtein for better short-token typo/prefix behaviour on product
/// names. Hand-rolled — no NuGet dependency.</para>
///
/// <para>Scale note: in-memory ranking is fine at household scale (hundreds–low thousands of products).
/// A DB trigram index is out of scope (FUTURE if catalogs grow large).</para>
/// </summary>
public static class ProductNameMatcher
{
    /// <summary>Minimum score for a result to be surfaced in the UI.</summary>
    public const double DisplayCutoff = 0.70;

    /// <summary>
    /// Stricter cutoff applied to <b>single-token</b> queries (plantry-fz3i). Mean-best Jaro-Winkler with a
    /// lone query token has nothing to average it down, so a short token spuriously clears 0.70 against many
    /// unrelated name-tokens (e.g. "speaker" surfaced "sea salt", "sirloin steak", "sparkling water"). A
    /// single-token query must reach the <b>strong</b> tier (0.85) to surface — exact / token-subset rungs
    /// still qualify (they score ≥ 0.90); only the fuzzy rung-4 noise is cut. Multi-token queries keep
    /// <see cref="DisplayCutoff"/>, since a spurious token match is diluted by the mean across tokens.
    /// </summary>
    public const double SingleTokenCutoff = 0.85;

    /// <summary>Maximum hits returned from <c>Rank</c> — caps an over-broad query so it cannot flood the listbox.</summary>
    public const int MaxResults = 8;

    // ─── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ranks <paramref name="candidates"/> against <paramref name="query"/> and returns the hits that clear
    /// the cutoff (<see cref="DisplayCutoff"/>, or <see cref="SingleTokenCutoff"/> for a one-token query),
    /// ordered score-desc then alphabetical and capped at <see cref="MaxResults"/>.
    /// Returns an empty list for a blank query.
    /// </summary>
    public static IReadOnlyList<MatchResult> Rank<T>(
        IEnumerable<T> candidates,
        Func<T, string> nameSelector,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normQuery = Normalize(query);
        var queryTokens = Tokenize(normQuery);

        if (queryTokens.Length == 0)
            return [];

        var cutoff = queryTokens.Length == 1 ? SingleTokenCutoff : DisplayCutoff;
        var results = new List<MatchResult>();

        foreach (var candidate in candidates)
        {
            var name = nameSelector(candidate);
            var score = Score(normQuery, queryTokens, name);
            if (score >= cutoff)
                results.Add(new MatchResult(name, score));
        }

        results.Sort(MatchResult.ByScoreDescThenAlpha);
        return results.Count > MaxResults ? results.GetRange(0, MaxResults) : results;
    }

    /// <summary>
    /// Ranks the <paramref name="namedItems"/> (pairs of id + name) and returns the hits above the cutoff
    /// (<see cref="DisplayCutoff"/>, or <see cref="SingleTokenCutoff"/> for a one-token query), ordered
    /// score-desc then alphabetical and capped at <see cref="MaxResults"/>. Returns an empty list for a blank query.
    /// </summary>
    public static IReadOnlyList<MatchResult<TId>> Rank<TId>(
        IEnumerable<(TId Id, string Name)> namedItems,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normQuery = Normalize(query);
        var queryTokens = Tokenize(normQuery);

        if (queryTokens.Length == 0)
            return [];

        var cutoff = queryTokens.Length == 1 ? SingleTokenCutoff : DisplayCutoff;
        var results = new List<MatchResult<TId>>();

        foreach (var (id, name) in namedItems)
        {
            var score = Score(normQuery, queryTokens, name);
            if (score >= cutoff)
                results.Add(new MatchResult<TId>(id, name, score));
        }

        results.Sort(MatchResult<TId>.ByScoreDescThenAlpha);
        return results.Count > MaxResults ? results.GetRange(0, MaxResults) : results;
    }

    /// <summary>
    /// Scores a single <paramref name="name"/> against <paramref name="query"/> and returns the
    /// score in [0, 1]. Does not apply the display cutoff — callers can decide whether to surface
    /// results at or below 0.70.
    /// </summary>
    public static double Score(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(name))
            return 0.0;

        var normQuery = Normalize(query);
        var queryTokens = Tokenize(normQuery);
        if (queryTokens.Length == 0) return 0.0;

        return Score(normQuery, queryTokens, name);
    }

    // ─── Scoring ───────────────────────────────────────────────────────────────

    private static double Score(string normQuery, string[] queryTokens, string name)
    {
        var normName = Normalize(name);
        var nameTokens = Tokenize(normName);

        // Rung 1: normalized strings equal after singularization (exact).
        // Singularization means "tomatos" and "tomatoes" join to the same root as "tomato".
        // Compare the re-joined singularized tokens (not the raw normalized strings) so that
        // plurals equate to their roots — the spec intent: "tomatos == Tomato → exact".
        var normQuerySing = string.Join(' ', queryTokens);
        var normNameSing  = string.Join(' ', nameTokens);
        if (normQuerySing == normNameSing) return 1.00;

        // Rung 2: token sets equal (word-order independent: "milk oat" == "Oat Milk").
        var querySet = new HashSet<string>(queryTokens);
        var nameSet = new HashSet<string>(nameTokens);
        if (querySet.SetEquals(nameSet)) return 0.95;

        // Rung 3: all query tokens are a subset of name tokens.
        if (queryTokens.All(qt => nameSet.Contains(qt))) return 0.90;

        // Rung 4: mean of per-query-token best Jaro-Winkler against name tokens.
        var mean = MeanBestJaroWinkler(queryTokens, nameTokens);

        // Substring boost: if the normalized name contains the normalized query, floor at 0.85.
        if (normName.Contains(normQuery, StringComparison.Ordinal) && mean < 0.85)
            mean = 0.85;

        return mean;
    }

    private static double MeanBestJaroWinkler(string[] queryTokens, string[] nameTokens)
    {
        if (nameTokens.Length == 0) return 0.0;

        var sum = 0.0;
        foreach (var qt in queryTokens)
        {
            var best = 0.0;
            foreach (var nt in nameTokens)
            {
                var jw = JaroWinkler(qt, nt);
                if (jw > best) best = jw;
            }
            sum += best;
        }

        return sum / queryTokens.Length;
    }

    // ─── Normalization ─────────────────────────────────────────────────────────

    /// <summary>Lowercase, strip punctuation to spaces. Exposed for unit testing.</summary>
    public static string Normalize(string s)
    {
        var lower = s.ToLowerInvariant();
        var chars = new char[lower.Length];
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            chars[i] = char.IsLetter(c) || char.IsDigit(c) ? c : ' ';
        }
        // Collapse multiple spaces into one and trim.
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Tokenize on whitespace, then singularize each token. Exposed for unit testing.</summary>
    public static string[] Tokenize(string normalized) =>
        normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Singularize)
            .ToArray();

    /// <summary>
    /// Simple English singularizer used to equate plurals with their roots.
    /// Rules applied in order (first match wins):
    /// <list type="bullet">
    /// <item><c>-ies</c> → <c>-y</c> (>4 chars, e.g. berries→berry)</item>
    /// <item><c>-es</c>  → strip (>4 chars, e.g. tomatoes→tomato, batches→batch)</item>
    /// <item><c>-s</c>   → strip (>3 chars, guards against "as"→"a" type breakage)</item>
    /// </list>
    /// Note: <c>-es</c> correctly handles words like "tomatoes" (8 chars → "tomato") and "potatoes"
    /// because stripping the last 2 chars preserves the root vowel.
    /// Not a linguistic singularizer — designed only for common English food names at household scale.
    /// Exposed for unit testing.
    /// </summary>
    public static string Singularize(string token)
    {
        if (token.EndsWith("ies") && token.Length > 4)
            return token[..^3] + "y";

        if (token.EndsWith("es") && token.Length > 4)
            return token[..^2];

        if (token.EndsWith("s") && token.Length > 3)
            return token[..^1];

        return token;
    }

    // ─── Jaro-Winkler ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hand-rolled Jaro-Winkler distance in [0, 1].
    /// Chosen over Levenshtein for better short-token prefix/typo behaviour on product name tokens.
    /// </summary>
    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1.0;

        var jaro = Jaro(s1, s2);
        if (jaro == 0.0) return 0.0;

        // Winkler prefix bonus: up to 4 common prefix characters, scaling factor p = 0.1.
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        return jaro + prefix * 0.1 * (1.0 - jaro);
    }

    private static double Jaro(string s1, string s2)
    {
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];

        var matches = 0;
        var transpositions = 0;

        // Count matches.
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, s2.Length);

            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        // Count transpositions.
        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        return (matches / (double)s1.Length
              + matches / (double)s2.Length
              + (matches - transpositions / 2.0) / matches)
             / 3.0;
    }

    // ─── Tier helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the display label for a ranked result: <c>"best"</c> for the top hit or any exact
    /// match (score == 1.0), otherwise <c>"N%"</c> (e.g. <c>"87%"</c>) for strong/near tiers.
    /// </summary>
    public static string RankLabel(double score, bool isTopHit) =>
        isTopHit || score >= 1.0 ? "best" : $"{(int)Math.Round(score * 100)}%";
}

// ─── Result types ──────────────────────────────────────────────────────────────

/// <summary>A scored match from <see cref="ProductNameMatcher.Rank{T}"/>.</summary>
public sealed class MatchResult(string name, double score)
{
    public string Name { get; } = name;
    public double Score { get; } = score;

    internal static readonly Comparison<MatchResult> ByScoreDescThenAlpha =
        (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        };
}

/// <summary>A scored match from <see cref="ProductNameMatcher.Rank{TId}"/>.</summary>
public sealed class MatchResult<TId>(TId id, string name, double score)
{
    public TId Id { get; } = id;
    public string Name { get; } = name;
    public double Score { get; } = score;

    internal static readonly Comparison<MatchResult<TId>> ByScoreDescThenAlpha =
        (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        };
}
