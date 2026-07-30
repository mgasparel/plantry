using System.Text.RegularExpressions;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Conventions;

/// <summary>
/// Source-scanning guard (plantry-qrg7) for a recurring defect class: <c>@Html.Raw(...)</c> spliced inside an
/// Alpine <c>x-data</c> attribute. <c>Html.Raw</c> bypasses Razor's HTML-encoding, so any <c>"</c> or <c>'</c>
/// produced by the serialized payload terminates the attribute early — the remainder of the Alpine object spills
/// onto the page as visible text, Alpine bindings go dead, and button labels render blank. This has recurred three
/// times (plantry-gcpb, plantry-wcmg x2); this guard exists to make a fourth impossible.
///
/// <para><b>Canonical safe form</b> (do not change it) — splice the serialized payload directly, with NO
/// <c>Html.Raw</c>:</para>
/// <code>x-data="{ unit: @JsonSerializer.Serialize(sheet.UnitCode) }"</code>
/// <para>Razor HTML-encodes the attribute value; the browser decodes entities in attribute context before Alpine
/// reads it, so <c>&amp;quot;</c> / <c>&amp;amp;</c> round-trip correctly. Rationale documented at
/// <c>Recipes/Edit.cshtml:74-77</c> and <c>Shared/_ProductSearchCreateSheet.cshtml:106-109</c>.</para>
///
/// <para><b>Deliberately out of scope</b> — non-<c>x-data</c> Alpine attributes (<c>x-show</c>, <c>x-text</c>,
/// <c>x-on:</c>, <c>:class</c>). All known sites splice either developer-authored compile-time constants or values
/// already escaped through <c>RazorJs.Literal</c>; no user-supplied string reaches any of them today. A related risk
/// — <c>RazorJs.Literal</c> escapes <c>\</c> and <c>'</c> but not <c>"</c>, while its output is spliced into
/// double-quoted <c>x-text</c> attributes — is tracked separately (plantry-97jd) and is intentionally not enforced
/// here.</para>
///
/// <para><b>Algorithm</b> — this cannot be a line-by-line predicate: a multi-line <c>x-data</c> attribute (opener on
/// one line, the offending <c>Html.Raw</c> seed many lines later, closing quote after that) means the seed line and
/// the attribute opener can be far apart. Instead this scans the whole file text as a sequence of regions:
/// <list type="number">
/// <item>Find each match of <c>x-data\s*=\s*(['"])</c>. Requiring the <c>=</c> and the quote keeps the scan off
/// prose mentions of the words "x-data" (comments, code-sample strings) that have no <c>=</c> immediately after.</item>
/// <item>Before matching, mask every quote character that appears inside a Razor code splice (<c>@(...)</c> or
/// <c>@Identifier.Path(...)</c>) with a neutral sentinel — see <see cref="MaskRazorSplices"/> — without changing
/// the text's length. This is necessary: splices routinely contain a literal <c>"</c> that is not an attribute
/// terminator, e.g. <c>@JsonSerializer.Serialize(Model.MoveInput.LocationId?.ToString() ?? "")</c>
/// (<c>_MoveSheet.cshtml:17</c>) and <c>@(Model.IsCreate ? "true" : "false")</c> (<c>Recipes/Edit.cshtml:205</c>).
/// Scanning unmasked text for the next quote character would terminate the region there — often many lines before
/// the attribute's real closing quote — silently blinding the guard to an <c>Html.Raw</c> call later in the same
/// attribute.</item>
/// <item>From the character after the opening quote, find the next occurrence of the SAME quote character in the
/// masked text — the attribute terminator. With splice-internal quotes masked out, the two quote styles now nest
/// correctly by convention: inside a double-quoted <c>x-data</c> the JS uses single-quoted string literals
/// (including escaped <c>\'</c>), and masked splices contribute no literal <c>"</c>. The converse holds for
/// single-quoted regions.</item>
/// <item>If the region contains <c>Html.Raw(</c>, record an offender at the line of the <c>Html.Raw</c> occurrence
/// (not the region opener), so the failure message points straight at the fix site.</item>
/// <item>Resume scanning after the region terminator. An unterminated region (no closing quote before EOF) is
/// reported as its own distinct, loud failure rather than silently swallowing the rest of the file.</item>
/// </list>
/// </summary>
public sealed class AlpineXDataRawGuardTests
{
    // Requires the '=' and the quote character so the scan does not fire on prose/comment mentions of the bare
    // word "x-data" (e.g. "// Alpine x-data (substitute ..." in Dev/Index.cshtml, or "x-data:" in a doc comment) —
    // those have no '=' immediately following and so never open a region.
    private static readonly Regex XDataAttributeOpener = new(@"x-data\s*=\s*(?<quote>[""'])", RegexOptions.Compiled);

    private static readonly Regex HtmlRawCall = new(@"Html\.Raw\(", RegexOptions.Compiled);

    private enum OffenseKind
    {
        HtmlRawInXData,
        UnterminatedXData,
    }

    private readonly record struct XDataOffense(int Line, OffenseKind Kind)
    {
        public override string ToString() => Kind switch
        {
            OffenseKind.HtmlRawInXData => $"line {Line}: Html.Raw(...) spliced inside an x-data attribute",
            OffenseKind.UnterminatedXData => $"line {Line}: x-data attribute has no matching closing quote (scan cannot verify it is safe)",
            _ => $"line {Line}: unknown offense",
        };
    }

    /// <summary>
    /// The single region-tracking predicate the tree scan and both theories share: scans <paramref name="text"/>
    /// (a whole file's contents, or a synthetic sample) for <c>x-data</c> attribute regions and reports every
    /// region that contains an <c>Html.Raw(</c> call, plus any region left unterminated at EOF.
    /// </summary>
    private static IReadOnlyList<XDataOffense> FindOffenses(string text)
    {
        // Mask quote characters that live inside Razor code splices before doing any region matching. Splices
        // routinely contain a literal '"' that is not an HTML attribute terminator (see MaskRazorSplices doc);
        // scanning the raw text would let that quote falsely end the x-data region early. Masking preserves the
        // text's length, so every index/line computed below is valid for both the masked and original text.
        var scan = MaskRazorSplices(text);
        var offenses = new List<XDataOffense>();
        var searchFrom = 0;

        while (true)
        {
            var opener = XDataAttributeOpener.Match(scan, searchFrom);
            if (!opener.Success)
                break;

            var quoteChar = opener.Groups["quote"].Value[0];
            var contentStart = opener.Index + opener.Length;
            var closeIndex = scan.IndexOf(quoteChar, contentStart);

            if (closeIndex < 0)
            {
                offenses.Add(new XDataOffense(LineNumberAt(text, opener.Index), OffenseKind.UnterminatedXData));
                break; // No terminator to resume after — nothing further can be safely scanned as "inside" vs "outside" this region.
            }

            var region = scan.Substring(contentStart, closeIndex - contentStart);
            var rawMatch = HtmlRawCall.Match(region);
            if (rawMatch.Success)
            {
                var absoluteIndex = contentStart + rawMatch.Index;
                offenses.Add(new XDataOffense(LineNumberAt(text, absoluteIndex), OffenseKind.HtmlRawInXData));
            }

            searchFrom = closeIndex + 1; // Resume scanning after the region terminator, not from the opener match end.
        }

        return offenses;
    }

    /// <summary>
    /// Returns a same-length copy of <paramref name="text"/> in which every quote character (<c>"</c> or <c>'</c>)
    /// occurring inside a Razor code splice's parenthesized expression — <c>@(...)</c> or
    /// <c>@Identifier.Path(...)</c> — is replaced with a neutral sentinel character. A bare member-access splice
    /// with no parens (<c>@Model.Foo</c>) has no parenthesized span and so is left untouched; it cannot contain a
    /// quote. <c>@@</c> (Razor's escaped literal <c>@</c>) is skipped, not treated as a splice. Preserving length
    /// keeps every index and line-number computation in <see cref="FindOffenses"/> valid against the original text,
    /// and masking only quote characters — never letters or parens — leaves an <c>Html.Raw(</c> call fully
    /// detectable in the masked text.
    ///
    /// <para>The paren-depth walk that finds the splice's matching close paren is string/char-literal aware: a
    /// <c>"</c> or <c>'</c> encountered while walking is not itself masked in place, but the depth counter skips
    /// over the whole literal (via <see cref="SkipVerbatimStringLiteral"/> / <see cref="SkipEscapableLiteral"/>)
    /// so a paren living inside a literal — e.g. <c>@Model.Name.Replace("(", "")</c> or
    /// <c>@Model.Name.Split('(')[0]</c> — never perturbs the depth count. Once the matching close paren is found,
    /// masking itself is unchanged: every quote character in the whole <c>[parenStart, k)</c> span — including
    /// ones inside literals — is masked, because it is all C# expression text, not HTML.</para>
    ///
    /// <para>The depth walk is NOT bounded to a single line — a legitimate splice routinely spans several lines
    /// with no verbatim string involved, e.g. <c>Pages/Today/_PlannedMealsBand.cshtml:128-129</c>
    /// (<c>@string.Join(" + ", slot.Dishes.Select(d =&gt;</c> / <c>$"{d.Name} ({...})"))</c>) and
    /// <c>:139-141</c> (a multi-line <c>@(cond ? $"…" : $"…")</c> ternary). Both contain interpolated string
    /// literals, which the literal skips above handle correctly regardless of how many lines they span.</para>
    ///
    /// <para>Without a line bound, an unclosed splice (e.g. a stray <c>@Broken(oops</c> with no matching close
    /// paren of its own) can wander arbitrarily far — across unrelated markup, potentially into a following
    /// <c>x-data</c> attribute — and re-balance on some wholly unrelated later <c>)</c>. Taking the balanced path
    /// for that wandered span would mask every quote in it, including the subsequent <c>x-data</c> attribute's own
    /// opening quote, silently blinding the guard to it. This is guarded against directly rather than by bounding
    /// the walk: a legitimate C# splice expression never itself contains an <c>x-data</c> attribute opener, so if
    /// the wandered <c>[parenStart, k)</c> span matches <see cref="XDataAttributeOpener"/>, the walk is treated as
    /// unbalanced regardless of the paren depth reaching zero.</para>
    ///
    /// <para>If the walk never returns to depth zero before EOF, or it does but the span swallowed an <c>x-data</c>
    /// opener, the splice is treated as malformed (e.g. a stray <c>@Broken(oops</c> with no closing paren at all).
    /// In that case NOTHING is masked for this splice and scanning resumes at <c>parenStart + 1</c>, not at EOF.
    /// Masking through EOF would blank every later quote in the file, which stops
    /// <see cref="XDataAttributeOpener"/> from matching any subsequent <c>x-data</c> attribute — silently blinding
    /// the guard to the rest of the file (proven against <c>Dev/Index.cshtml</c>, which holds 11 <c>x-data</c>
    /// attributes: a single unrelated <c>@string.Concat("Total (net")</c>-shaped line elsewhere in the file hid a
    /// genuine <c>Html.Raw</c> offender with zero reported offenses). Refusing to mask on either failure mode means
    /// the walk can neither mask to EOF nor silently re-balance across an <c>x-data</c> boundary — at worst it
    /// leaves an ordinary unmasked quote to be discovered by the normal region scan, surfacing as a loud
    /// <see cref="OffenseKind.UnterminatedXData"/> or an early region termination at a real quote character, never
    /// a silent pass.</para>
    /// </summary>
    private static string MaskRazorSplices(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;

        while (i < chars.Length)
        {
            if (chars[i] != '@')
            {
                i++;
                continue;
            }

            if (i + 1 < chars.Length && chars[i + 1] == '@')
            {
                i += 2; // "@@" is Razor's escaped literal '@', not a code splice.
                continue;
            }

            var j = i + 1;

            // Walk an identifier / dotted-member-access chain: @Foo, @Foo.Bar, @Foo.Bar.Baz. @(...) skips this
            // loop entirely (the char right after '@' is '(', not part of an identifier).
            while (j < chars.Length && (char.IsLetterOrDigit(chars[j]) || chars[j] == '_' || chars[j] == '.'))
                j++;

            if (j >= chars.Length || chars[j] != '(')
            {
                // A bare member-access splice with no call/parenthesized expression — no quotes possible.
                i = i + 1 > j ? i + 1 : j;
                continue;
            }

            var parenStart = j;
            var depth = 0;
            var k = parenStart;
            var balanced = false;
            for (; k < chars.Length; k++)
            {
                // String/char-literal-aware: a paren living inside a literal (e.g. Replace("(", "") or
                // Split('(')[0]) must not perturb depth, so skip the whole literal without counting its contents.
                // Safe to check chars[k - 1]: k starts at parenStart (the '(' itself), so a quote check only
                // fires once k > parenStart >= 1.
                if (chars[k] == '"' && k > parenStart && chars[k - 1] == '@')
                {
                    k = SkipVerbatimStringLiteral(chars, k);
                    continue;
                }

                if (chars[k] == '"')
                {
                    k = SkipEscapableLiteral(chars, k, '"');
                    continue;
                }

                if (chars[k] == '\'')
                {
                    k = SkipEscapableLiteral(chars, k, '\'');
                    continue;
                }

                if (chars[k] == '(')
                {
                    depth++;
                }
                else if (chars[k] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        k++; // Move past the matching close paren.
                        balanced = true;
                        break;
                    }
                }
            }

            // A legitimate splice expression never contains an x-data attribute opener. If the walk only reached
            // depth 0 by wandering across one (the unclosed-splice case: '@Broken(oops' re-balancing on some
            // unrelated later ')'), refuse to mask: masking that span would blank the x-data attribute's own
            // opening quote and silently blind the guard to it.
            if (balanced && XDataAttributeOpener.IsMatch(new string(chars, parenStart, k - parenStart)))
                balanced = false;

            if (balanced)
            {
                // Mask every quote inside the parenthesized span, including ones inside literals — it is all C#
                // expression text, not HTML, so no quote in this span can be a real attribute terminator.
                for (var m = parenStart; m < k && m < chars.Length; m++)
                {
                    if (chars[m] is '"' or '\'')
                        chars[m] = '￿';
                }

                i = k;
            }
            else
            {
                // Unbalanced splice (reached EOF with depth > 0, or ran off the end of an unterminated literal):
                // mask NOTHING and resume right after the opening paren. Masking through EOF would blank every
                // later quote in the file and silently blind the guard to everything after this splice — see the
                // class doc above.
                i = parenStart + 1;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// From the opening quote at <paramref name="open"/> (a verbatim-string quote, i.e. immediately preceded by
    /// <c>@</c>), returns the index of the matching closing quote, treating <c>""</c> as an escaped quote pair
    /// that does not close the literal. Returns <c>chars.Length</c> if the literal never closes.
    /// </summary>
    private static int SkipVerbatimStringLiteral(char[] chars, int open)
    {
        var p = open + 1;
        while (p < chars.Length)
        {
            if (chars[p] == '"')
            {
                if (p + 1 < chars.Length && chars[p + 1] == '"')
                {
                    p += 2; // "" is an escaped quote inside a verbatim string; keep scanning.
                    continue;
                }
                return p;
            }
            p++;
        }
        return chars.Length; // Never closes.
    }

    /// <summary>
    /// From the opening delimiter at <paramref name="open"/> (a regular string <c>"</c> or char literal <c>'</c>),
    /// returns the index of the matching closing <paramref name="delimiter"/>, treating a backslash as escaping
    /// the next character (so <c>\"</c> / <c>\'</c> do not close the literal). Returns <c>chars.Length</c> if the
    /// literal never closes.
    /// </summary>
    private static int SkipEscapableLiteral(char[] chars, int open, char delimiter)
    {
        var p = open + 1;
        while (p < chars.Length)
        {
            if (chars[p] == '\\')
            {
                p += 2; // Backslash escapes the next char; consume both as a pair.
                continue;
            }
            if (chars[p] == delimiter)
                return p;
            p++;
        }
        return chars.Length; // Never closes.
    }

    private static int LineNumberAt(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    [Fact(DisplayName = "No Html.Raw spliced inside an x-data attribute anywhere in Plantry.Web")]
    public void PlantryWeb_HasNoHtmlRawInsideXData()
    {
        var webRoot = Path.Combine(WebSourceTree.RepoRoot(), "src", "Plantry.Web");

        var offenders = new List<string>();
        foreach (var file in WebSourceTree.EnumerateSourceFiles(webRoot))
        {
            var text = File.ReadAllText(file);
            foreach (var offense in FindOffenses(text))
                offenders.Add($"{file}:{offense}");
        }

        Assert.True(
            offenders.Count == 0,
            "Html.Raw must never be spliced inside an x-data attribute — it bypasses Razor's HTML-encoding and " +
            "the resulting raw quote truncates the attribute (plantry-gcpb, plantry-wcmg, plantry-qrg7). Use " +
            "JsonSerializer.Serialize(...) directly with no Html.Raw wrapper. Offenders:\n" +
            string.Join("\n", offenders));
    }

    // Every shape the guard exists to catch: single-line and multi-line, double- and single-quoted, with
    // whitespace around '=', and a bare function-call splice (not just an object literal).
    public static IEnumerable<object[]> PositiveShapes()
    {
        // a. single-line double-quoted
        yield return new object[]
        {
            """<div x-data="{ unit: @Html.Raw(JsonSerializer.Serialize(c)) }">"""
        };

        // b. multi-line double-quoted — the _AmendSheet shape: opener, several benign lines, then the Html.Raw
        // seed, then the closing quote several lines later.
        yield return new object[]
        {
            """
            <form hx-post="/x"
                  x-data="{
                    price: @JsonSerializer.Serialize(sheet.Price),
                    entered: @Html.Raw(JsonSerializer.Serialize(sheet.EnteredQuantity)),
                    unit: @JsonSerializer.Serialize(sheet.UnitCode),

                    fmt(n) { return n.toString() }
                  }">
            """
        };

        // c. single-quoted
        yield return new object[]
        {
            "<div x-data='takeStockLotPanel(@Html.Raw(BuildLotJson(Model)))'>"
        };

        // d. whitespace around the equals
        yield return new object[]
        {
            """<div x-data = "{ a: @Html.Raw(x) }">"""
        };

        // e. function-call form, not an object literal
        yield return new object[]
        {
            """<div x-data="planTune(@Html.Raw(demoCfgJson))">"""
        };

        // f. a @(...) ternary splice containing a literal quote, alongside a genuine Html.Raw offender later in
        // the same region — mirrors Recipes/Edit.cshtml:205 (isCreate: @(Model.IsCreate ? "true" : "false")).
        // Pins that a quote produced by the splice itself is masked before region-terminator matching, so it
        // cannot falsely end the region before the Html.Raw seed is scanned.
        yield return new object[]
        {
            """<div x-data="{ isCount: @(Model.IsCreate ? "true" : "false"), seed: @Html.Raw(json) }">"""
        };

        // g. a JsonSerializer.Serialize(... ?? "") splice containing a literal empty-string default, alongside a
        // genuine Html.Raw offender later in the same region — mirrors _MoveSheet.cshtml:17
        // (dest: @JsonSerializer.Serialize(Model.MoveInput.LocationId?.ToString() ?? "")).
        yield return new object[]
        {
            """<div x-data="{ dest: @JsonSerializer.Serialize(s ?? ""), seed: @Html.Raw(json) }">"""
        };

        // h. a splice containing an unmatched '(' inside a regular string literal argument
        // (@Model.Name.Replace("(", "")), followed by a genuine Html.Raw offender in a later x-data — pins that
        // the paren-depth walk skips the literal's contents instead of letting its "(" unbalance the walk and
        // mask through EOF, which would otherwise hide the offender below.
        yield return new object[]
        {
            """
            <p>@Model.Name.Replace("(", "")</p>
            <div x-data="{ seed: @Html.Raw(json) }">
            """
        };

        // i. the same hole via a char literal (@Model.Name.Split('(')[0]) instead of a string literal.
        yield return new object[]
        {
            """
            <p>@Model.Name.Split('(')[0]</p>
            <div x-data="{ seed: @Html.Raw(json) }">
            """
        };

        // j. a @(...) ternary splice whose literal contains a ')' (@(x ? "a)" : "b")), alongside a genuine
        // Html.Raw offender in the same x-data region — pins that the ')' inside the literal does not end the
        // paren-depth walk early (which would leave the literal's own closing quote unmasked and falsely
        // terminate the x-data region before the Html.Raw seed).
        yield return new object[]
        {
            """<div x-data="{ a: @(x ? "a)" : "b"), c: @Html.Raw(json) }">"""
        };

        // k. a splice that never closes at all (@Broken(oops with no matching ')' on its own), followed by a
        // genuine Html.Raw offender in a later x-data, followed by a trailing unrelated ')' — pins the
        // unbalanced-splice rule against BOTH failure modes: an unterminated splice must mask nothing and resume
        // scanning right after the opening paren (not mask through EOF, which would silently blind the guard to
        // the offender below), AND wandering across the x-data attribute to re-balance on the trailing ')' must
        // be refused (the wandered span contains an x-data opener, so it is treated as unbalanced too) rather
        // than masking the x-data's own opening quote.
        yield return new object[]
        {
            """
            <p>@Broken(oops</p>
            <div x-data="{ seed: @Html.Raw(json) }">
            <p>trailing ) paren</p>
            """
        };

        // l. a legitimate multi-line splice containing a regular (non-verbatim) string literal with a space —
        // mirrors Pages/Today/_PlannedMealsBand.cshtml:128-129 (@string.Join(" + ", slot.Dishes.Select(d =>
        // d.Name)), spanning two lines) — alongside a genuine Html.Raw offender later in the same x-data region.
        // Pins that the walk is not line-bounded: a legitimate splice may itself span multiple lines and must
        // still be masked correctly so its internal quote does not falsely terminate the region before the seed.
        yield return new object[]
        {
            """
            <div class="band"
                 x-data="{
                    label: @string.Join(" + ", Model.Dishes.Select(d =>
                                d.Name)),
                    seed: @Html.Raw(json)
                 }">
            """
        };
    }

    [Theory(DisplayName = "Guard flags every Html.Raw-inside-x-data shape")]
    [MemberData(nameof(PositiveShapes))]
    public void Positive_ForbiddenShapes_AreDetected(string sample) =>
        Assert.True(FindOffenses(sample).Any(o => o.Kind == OffenseKind.HtmlRawInXData),
            $"Guard should have flagged: {sample}");

    // Shapes that superficially resemble the forbidden pattern (word "x-data" without '=', Html.Raw elsewhere in
    // the same file but never inside an x-data region, non-x-data Alpine attributes, the canonical safe form) and
    // must NOT be flagged. These pin the guard's precision.
    public static IEnumerable<object[]> NegativeShapes()
    {
        // a. script-tag JSON payload — Html.Raw is legitimate here, and there is no x-data anywhere nearby.
        yield return new object[]
        {
            """<script type="application/json" id="d">@Html.Raw(Model.Json)</script>"""
        };

        // b. module import URL
        yield return new object[]
        {
            "const { m } = await import('@Html.Raw(src)');"
        };

        // c. OOB attribute splice — no x-data attribute is present at all.
        yield return new object[]
        {
            """<div id="x"@Html.Raw(oobAttr)>"""
        };

        // d. the _ShoppingItem.cshtml:39 comment shape — a "//" comment inside an @{ } block that literally
        // contains the words "Html.Raw(JsonSerializer...) inside a double-quoted x-data attribute", but "x-data"
        // is never followed by '=' so no region opens.
        yield return new object[]
        {
            """
            @{
                // Alpine x-data: use JsonSerializer so Razor HTML-encodes the quotes correctly.
                // Never use @Html.Raw(JsonSerializer...) inside a double-quoted x-data attribute.
            }
            """
        };

        // e. non-x-data Alpine attribute — x-show, not x-data.
        yield return new object[]
        {
            """<p x-show="@Html.Raw(Model.EchoShowExpression)">"""
        };

        // f. the canonical correct form — JsonSerializer.Serialize with no Html.Raw.
        yield return new object[]
        {
            """<div x-data="{ unit: @JsonSerializer.Serialize(c) }">"""
        };

        // g. the Dev/Index.cshtml:1599 shape — the bare word "x-data" in a comment with no '=', on a line that
        // also contains the text "Html.Raw" elsewhere (in an unrelated comment about how NOT to do it).
        yield return new object[]
        {
            "// Alpine x-data (substitute serialised JSON): see 'without Html.Raw' note above for the pattern."
        };

        // h. a benign multi-line x-data region that closes, followed later in the same file by a legitimate
        // Html.Raw script payload — pins that the region terminates at its closing quote and the scan does not
        // run away to EOF treating everything after the opener as still "inside" x-data.
        yield return new object[]
        {
            """
            <div x-data="{
                unit: @JsonSerializer.Serialize(sheet.UnitCode),
                price: @JsonSerializer.Serialize(sheet.Price)
              }">
            </div>
            <script type="application/json" id="payload">@Html.Raw(Model.Json)</script>
            """
        };

        // i. the @(...) ternary splice from positive shape (f), with the Html.Raw offender removed — must not be
        // flagged even though the splice contains a literal quote character.
        yield return new object[]
        {
            """<div x-data="{ isCount: @(Model.IsCreate ? "true" : "false") }">"""
        };

        // j. the JsonSerializer.Serialize(... ?? "") splice from positive shape (g), with no Html.Raw inside the
        // x-data region, followed by a legitimate Html.Raw script payload AFTER the attribute closes — pins that
        // masking the splice's internal quotes does not over-extend the region past its real closing quote.
        yield return new object[]
        {
            """
            <div x-data="{ dest: @JsonSerializer.Serialize(s ?? "") }"></div>
            <script type="application/json" id="payload">@Html.Raw(Model.Json)</script>
            """
        };

        // k. positive shape (h) with the offender replaced by the safe form — pins that the literal-aware skip
        // does not turn a benign string-literal '(' into a false positive.
        yield return new object[]
        {
            """
            <p>@Model.Name.Replace("(", "")</p>
            <div x-data="{ seed: @JsonSerializer.Serialize(json) }">
            """
        };

        // l. positive shape (i) with the offender replaced by the safe form — same, via a char literal.
        yield return new object[]
        {
            """
            <p>@Model.Name.Split('(')[0]</p>
            <div x-data="{ seed: @JsonSerializer.Serialize(json) }">
            """
        };

        // m. positive shape (j) with the offender replaced by the safe form — pins that the ')' inside the
        // ternary's literal does not falsely terminate the region even when there is no Html.Raw to find.
        yield return new object[]
        {
            """<div x-data="{ a: @(x ? "a)" : "b"), c: @JsonSerializer.Serialize(json) }">"""
        };

        // n. positive shape (k) with the offender replaced by the safe form — pins that resuming scanning past an
        // unbalanced splice's opening paren, and refusing to mask a wandered span that swallows an x-data
        // opener, does not invent a failure either (including in the presence of the trailing unrelated ')').
        yield return new object[]
        {
            """
            <p>@Broken(oops</p>
            <div x-data="{ seed: @JsonSerializer.Serialize(json) }">
            <p>trailing ) paren</p>
            """
        };

        // o. positive shape (l) with the offender replaced by the safe form — pins that a legitimate multi-line
        // splice's internal quote does not falsely terminate the region even when there is no Html.Raw to find.
        yield return new object[]
        {
            """
            <div class="band"
                 x-data="{
                    label: @string.Join(" + ", Model.Dishes.Select(d =>
                                d.Name)),
                    seed: @JsonSerializer.Serialize(json)
                 }">
            """
        };
    }

    [Theory(DisplayName = "Guard does not flag benign patterns that merely resemble Html.Raw-inside-x-data")]
    [MemberData(nameof(NegativeShapes))]
    public void Negative_BenignPatterns_AreNotFlagged(string sample) =>
        // Assert zero offenses of ANY kind, not just HtmlRawInXData — sample (h) exists specifically to pin that
        // a benign multi-line x-data region terminates correctly, so this must also catch a regression where
        // terminator detection breaks and the region is reported as UnterminatedXData instead.
        Assert.Empty(FindOffenses(sample));

    [Fact(DisplayName = "Guard reports an unterminated x-data attribute as its own loud failure")]
    public void UnterminatedXData_IsReportedDistinctly()
    {
        var sample = """<div x-data="{ unit: @JsonSerializer.Serialize(c) }""" + "\n<p>no closing quote above</p>";

        var offenses = FindOffenses(sample);

        Assert.Contains(offenses, o => o.Kind == OffenseKind.UnterminatedXData);
    }
}
