namespace Plantry.Web.Dev;

/// <summary>
/// One dev-only endpoint, as recorded by <see cref="DevEndpointRouteBuilderExtensions.MapDevPost"/>.
/// </summary>
/// <param name="Method">HTTP method (always <c>POST</c> today).</param>
/// <param name="Path">Route pattern the endpoint is mapped at, e.g. <c>/Dev/Reset</c>.</param>
/// <param name="Description">One-line blurb of what triggering the endpoint does.</param>
/// <param name="Destructive">
/// True for anything that wipes or irreversibly mutates data — the /Dev/Endpoints page renders
/// these with danger styling and a confirm before firing.
/// </param>
public sealed record DevEndpoint(string Method, string Path, string Description, bool Destructive);

/// <summary>
/// Registry of dev-only endpoints, populated as a side effect of mapping them through
/// <see cref="DevEndpointRouteBuilderExtensions.MapDevPost"/>. The <c>/Dev/Endpoints</c> reference
/// page renders this list, so every endpoint mapped through the helper appears there automatically —
/// no page edit is needed when a new dev endpoint is added.
///
/// Registered as a singleton. Entries are added once at startup while routes are mapped; the
/// collection is only read thereafter (per request, when the page renders).
/// </summary>
public sealed class DevEndpointRegistry
{
    private readonly List<DevEndpoint> _endpoints = [];

    /// <summary>Records a dev endpoint. Called by <c>MapDevPost</c> at route-mapping time.</summary>
    public void Add(DevEndpoint endpoint) => _endpoints.Add(endpoint);

    /// <summary>All registered dev endpoints, ordered by path for a stable listing.</summary>
    public IReadOnlyList<DevEndpoint> Endpoints =>
        _endpoints.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
}
