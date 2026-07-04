using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Load-bearing coverage for <c>CanonicaliseSectionOrder</c> (Edit.cshtml.cs) plus the caller's
/// contiguous-ordinal renumber (plantry-21if). The existing editor snapshots use
/// <see cref="RecipeEditorFixture.BuildRich"/>, whose stored order is already canonical — so the
/// reorder pass is a no-op there and a regression would not turn any snapshot red.
///
/// <para>This test drives the REAL edit GET (<c>/Recipes/{id}/Edit</c>) for a deliberately
/// non-canonical recipe (<see cref="RecipeEditorFixture.BuildNonCanonical"/>) whose stored ingredients
/// interleave headings (Sauce / Ungrouped / Topping / Sauce) and place ungrouped rows at Ordinal &gt; 0.
/// It parses the Alpine <c>x-data</c> <c>rows</c> initialiser and asserts the reordered output rather than
/// a brittle full-HTML snapshot: ungrouped-first, headings in first-appearance order, within-section
/// order preserved, and ordinals renumbered contiguous 0..N.</para>
///
/// <para>Because the assertions pin the exact reordered sequence, breaking either the ungrouped-first
/// hoist or the first-seen heading pass in <c>CanonicaliseSectionOrder</c> turns this test red — the
/// method it covers is otherwise untested. The method itself stays private; coverage is end-to-end.</para>
/// </summary>
public sealed class RecipeEditorCanonicaliseOrderTests(RecipeEditorFragmentFactory factory)
    : IClassFixture<RecipeEditorFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    /// <summary>One parsed ingredient row from the Alpine x-data initialiser.</summary>
    private readonly record struct EditorRow(int Ordinal, string ProductName, string GroupHeading);

    private async Task<IReadOnlyList<EditorRow>> GetEditorRowsAsync(Guid recipeId)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeEditorFixture.HouseholdAId.ToString());

        var response = await client.GetAsync($"/Recipes/{recipeId}/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var editor = doc.QuerySelector("#recipe-editor")
            ?? throw new InvalidOperationException("#recipe-editor not found in page HTML.");
        // GetAttribute returns the HTML-entity-decoded value, so the embedded JSON is intact.
        var xData = editor.GetAttribute("x-data")
            ?? throw new InvalidOperationException("x-data attribute not found on #recipe-editor.");

        var rowsJson = ExtractJsonArrayAfterKey(xData, "rows:");
        using var parsed = JsonDocument.Parse(rowsJson);

        return parsed.RootElement.EnumerateArray()
            .Select(e => new EditorRow(
                e.GetProperty("ordinal").GetInt32(),
                e.GetProperty("productName").GetString() ?? "",
                e.GetProperty("groupHeading").GetString() ?? ""))
            .ToList();
    }

    /// <summary>
    /// Extracts the JSON array literal that follows <paramref name="key"/> inside the Alpine x-data
    /// object. The x-data value is a JavaScript object literal (unquoted keys, function members), so it
    /// is not parseable as a whole; only the <c>rows</c> member is a JsonSerializer-emitted JSON array.
    /// Walks the array with string/escape awareness so quoted values containing brackets never confuse
    /// the bracket-depth balance.
    /// </summary>
    private static string ExtractJsonArrayAfterKey(string xData, string key)
    {
        var keyIdx = xData.IndexOf(key, StringComparison.Ordinal);
        Assert.True(keyIdx >= 0, $"'{key}' not found in x-data.");

        var start = xData.IndexOf('[', keyIdx + key.Length);
        Assert.True(start >= 0, $"No '[' after '{key}' in x-data.");

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < xData.Length; i++)
        {
            var c = xData[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '[': depth++; break;
                case ']':
                    depth--;
                    if (depth == 0)
                        return xData.Substring(start, i - start + 1);
                    break;
            }
        }

        throw new InvalidOperationException($"Unbalanced JSON array after '{key}' in x-data.");
    }

    /// <summary>
    /// AC: loading the edit GET for the non-canonical recipe returns rows that are ungrouped-first,
    /// headings in first-appearance order, within-section order preserved, and ordinals contiguous 0..N.
    /// Expected reorder of the stored order (Tomato/Sauce, Pasta/ungrouped, Chili/Topping, Garlic/Sauce,
    /// Salt/ungrouped):
    ///   0 Pasta  (ungrouped)
    ///   1 Salt   (ungrouped)
    ///   2 Tomato (Sauce   — first-seen heading, first member by ordinal)
    ///   3 Garlic (Sauce   — second member by ordinal)
    ///   4 Chili  (Topping — second-seen heading)
    /// </summary>
    [Fact]
    public async Task Edit_get_canonicalises_non_canonical_stored_order()
    {
        var rows = await GetEditorRowsAsync(RecipeEditorFixture.NonCanonicalRecipeId.Value);

        Assert.Equal(5, rows.Count);

        // (1) Exact reordered sequence — the strongest load-bearing assertion. Product names come from
        //     RecipeEditorFixture.ProductSummaries().
        Assert.Equal(
            new[] { "Rigatoni", "Salt", "Canned Tomatoes", "Garlic Cloves", "Dried Chili" },
            rows.Select(r => r.ProductName).ToArray());

        // (2) Ungrouped-first: every ungrouped row precedes every grouped row.
        var lastUngroupedIdx = rows
            .Select((r, i) => (r, i))
            .Where(x => x.r.GroupHeading.Length == 0)
            .Select(x => x.i)
            .DefaultIfEmpty(-1)
            .Max();
        var firstGroupedIdx = rows
            .Select((r, i) => (r, i))
            .Where(x => x.r.GroupHeading.Length > 0)
            .Select(x => x.i)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        Assert.True(
            lastUngroupedIdx < firstGroupedIdx,
            "Expected all ungrouped rows to precede all grouped rows.");
        Assert.Equal(2, rows.Count(r => r.GroupHeading.Length == 0)); // Pasta + Salt

        // (3) Headings appear in first-appearance order of the STORED recipe: Sauce (stored ord 0)
        //     before Topping (stored ord 2).
        var headingOrder = rows
            .Where(r => r.GroupHeading.Length > 0)
            .Select(r => r.GroupHeading)
            .Distinct()
            .ToArray();
        Assert.Equal(new[] { "Sauce", "Topping" }, headingOrder);

        // (4) Within-section order preserved: within "Sauce", Tomato (stored ord 0) precedes
        //     Garlic (stored ord 3).
        var sauceNames = rows.Where(r => r.GroupHeading == "Sauce").Select(r => r.ProductName).ToArray();
        Assert.Equal(new[] { "Canned Tomatoes", "Garlic Cloves" }, sauceNames);

        // (5) Ordinals renumbered contiguous 0..N in row order (the caller's Ordinal = idx reassignment).
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(i, rows[i].Ordinal);
    }
}
