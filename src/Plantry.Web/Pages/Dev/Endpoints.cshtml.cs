using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Web.Dev;

namespace Plantry.Web.Pages.Dev;

/// <summary>
/// Dev-only reference page (gated by <see cref="DevPagesGateMiddleware"/>) that lists every dev-only
/// endpoint mapped through <see cref="DevEndpointRouteBuilderExtensions.MapDevPost"/>. The list is
/// sourced from the <see cref="DevEndpointRegistry"/>, so a new endpoint added via MapDevPost appears
/// here with no edit to this page. Each row can be invoked inline (htmx POST) for quick reference.
/// </summary>
public sealed class EndpointsModel(DevEndpointRegistry registry) : PageModel
{
    /// <summary>The registered dev endpoints, ordered by path (see <see cref="DevEndpointRegistry"/>).</summary>
    public IReadOnlyList<DevEndpoint> Endpoints { get; private set; } = [];

    public void OnGet() => Endpoints = registry.Endpoints;
}
