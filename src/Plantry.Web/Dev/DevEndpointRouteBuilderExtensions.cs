using Microsoft.Extensions.DependencyInjection;

namespace Plantry.Web.Dev;

/// <summary>
/// Mapping helper for dev-only POST endpoints.
/// </summary>
public static class DevEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a dev-only POST endpoint AND records it in the <see cref="DevEndpointRegistry"/> so it
    /// auto-appears on the <c>/Dev/Endpoints</c> reference page — no page edit is needed when a new
    /// dev endpoint is added.
    ///
    /// CONVENTION: every dev-only endpoint MUST be mapped through this helper (not
    /// <c>app.MapPost</c> directly). That is what keeps the <c>/Dev/Endpoints</c> listing complete.
    /// Pass a one-line <paramref name="description"/> of what triggering the endpoint does, and set
    /// <paramref name="destructive"/> for anything that wipes or irreversibly mutates data (the page
    /// renders those with danger styling and a confirm before firing).
    /// </summary>
    /// <param name="endpoints">The route builder (the <c>WebApplication</c>).</param>
    /// <param name="pattern">Route pattern, e.g. <c>/Dev/Reset</c>. Also shown on the page.</param>
    /// <param name="handler">The request delegate to invoke.</param>
    /// <param name="description">One-line blurb of what triggering the endpoint does.</param>
    /// <param name="destructive">True if it wipes or irreversibly mutates data.</param>
    public static RouteHandlerBuilder MapDevPost(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Delegate handler,
        string description,
        bool destructive = false)
    {
        endpoints.ServiceProvider
            .GetRequiredService<DevEndpointRegistry>()
            .Add(new DevEndpoint("POST", pattern, description, destructive));

        return endpoints.MapPost(pattern, handler);
    }
}
