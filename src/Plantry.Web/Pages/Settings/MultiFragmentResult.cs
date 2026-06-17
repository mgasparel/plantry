using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// Renders a primary htmx fragment and zero or more OOB (hx-swap-oob="outerHTML") fragments
/// into a single HTML response body, letting htmx update multiple independent DOM nodes with
/// one POST — the primary fragment via its hx-target, the OOB fragments via the id-matched
/// elements they carry.
///
/// Pattern: the primary partial is rendered first (no OOB attribute); each subsequent partial
/// has hx-swap-oob="outerHTML" injected onto the element that carries the target id, allowing
/// htmx to find and replace the matching element in the live DOM.
/// </summary>
internal sealed class MultiFragmentResult : IActionResult
{
    private readonly PartialViewResult _primary;
    private readonly PartialViewResult[] _oobFragments;

    public MultiFragmentResult(PartialViewResult primary, params PartialViewResult?[] oobFragments)
    {
        _primary = primary;
        _oobFragments = oobFragments.OfType<PartialViewResult>().ToArray();
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var serviceProvider = context.HttpContext.RequestServices;
        var viewEngine = serviceProvider.GetRequiredService<ICompositeViewEngine>();
        var tempDataProvider = serviceProvider.GetRequiredService<ITempDataProvider>();

        var sb = new StringBuilder();

        // Render the primary fragment (no OOB modification).
        sb.Append(await RenderPartialAsync(context, viewEngine, tempDataProvider, _primary, oob: false));

        // Render each OOB fragment with hx-swap-oob injected onto its root element.
        foreach (var oob in _oobFragments)
            sb.Append(await RenderPartialAsync(context, viewEngine, tempDataProvider, oob, oob: true));

        var html = sb.ToString();
        context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static async Task<string> RenderPartialAsync(
        ActionContext context,
        ICompositeViewEngine viewEngine,
        ITempDataProvider tempDataProvider,
        PartialViewResult partial,
        bool oob)
    {
        // Resolve the view.
        var viewResult = viewEngine.FindView(context, partial.ViewName!, isMainPage: false);
        if (!viewResult.Success)
            throw new InvalidOperationException(
                $"Could not find partial view '{partial.ViewName}'. " +
                $"Searched: {string.Join(", ", viewResult.SearchedLocations ?? [])}");

        var viewData = partial.ViewData ?? new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            context.ModelState)
        {
            Model = partial.Model
        };

        var tempData = new TempDataDictionary(context.HttpContext, tempDataProvider);

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            context,
            viewResult.View,
            viewData,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        var html = writer.ToString();

        // For OOB fragments, inject hx-swap-oob="outerHTML" onto the root element so that
        // htmx can locate the matching id in the DOM and replace it.
        if (oob)
            html = InjectOobAttribute(html);

        return html;
    }

    /// <summary>
    /// Injects <c>hx-swap-oob="outerHTML"</c> onto the first opening tag of the HTML fragment.
    /// This assumes the fragment has a single root element (the Razor partial convention).
    /// </summary>
    private static string InjectOobAttribute(string html)
    {
        // Find the first '>' that closes the root element's opening tag.
        // We insert before the '>' to avoid breaking self-closing tags.
        var trimmed = html.TrimStart();
        if (!trimmed.StartsWith('<'))
            return html; // Safety: no tag found, return as-is.

        // Find the end of the opening tag (first '>').
        var tagEnd = trimmed.IndexOf('>');
        if (tagEnd < 0)
            return html;

        // Handle self-closing tags (shouldn't happen for OOB targets but guard anyway).
        if (trimmed[tagEnd - 1] == '/')
            return html;

        // Count leading whitespace to preserve it.
        var leadingWhitespace = html[..(html.Length - trimmed.Length)];

        return leadingWhitespace
               + trimmed[..tagEnd]
               + @" hx-swap-oob=""outerHTML"""
               + trimmed[tagEnd..];
    }
}
