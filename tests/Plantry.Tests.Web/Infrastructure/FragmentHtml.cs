using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Parses a rendered HTML response and extracts the individual htmx fragments of the review form, so each can
/// be snapshotted in isolation (the per-row swap target, the commit bar). The fragments are exactly the markup
/// the page renders for each line state; isolating them keeps a snapshot focused on the one fragment under test
/// rather than the whole page chrome.
/// </summary>
public static class FragmentHtml
{
    private static readonly HtmlParser Parser = new();

    /// <summary>The single <c>_ReviewRow</c> fragment for the line whose review-row DOM id is
    /// <c>import-line-{lineId}</c>.</summary>
    public static string Row(string pageHtml, Guid lineId)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var row = doc.GetElementById($"import-line-{lineId}")
            ?? throw new InvalidOperationException($"No review row found for line {lineId}.");
        return Pretty(row);
    }

    /// <summary>The commit / progress bar fragment.</summary>
    public static string CommitBar(string pageHtml)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var bar = doc.QuerySelector(".commit-bar")
            ?? throw new InvalidOperationException("No commit bar found.");
        return Pretty(bar);
    }

    /// <summary>The whole rows container — useful to assert ordering/count across states in one snapshot.</summary>
    public static string AllRows(string pageHtml)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var rows = doc.QuerySelector(".rev-list")
            ?? throw new InvalidOperationException("No review rows container found.");
        return Pretty(rows);
    }

    private static string Pretty(IElement element)
    {
        using var writer = new StringWriter();
        element.ToHtml(writer, new PrettyMarkupFormatter());
        return writer.ToString().Replace("\r\n", "\n").Trim();
    }
}
