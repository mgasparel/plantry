using System.Net;
using System.Net.Http.Headers;

namespace Plantry.Tests.Unit.Grocy.Fixtures;

/// <summary>
/// A dependency-free <see cref="HttpMessageHandler"/> that maps request paths to canned
/// response bodies. Registered as the inner handler when constructing a test <see cref="HttpClient"/>
/// for <c>GrocyClient</c>.
///
/// Path matching strips the leading slash and uses <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// Unregistered paths return HTTP 404 so tests fail with a clear diagnostic rather than a null ref.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a canned JSON body for the given <paramref name="path"/>.
    /// The path is the request path after the base address (e.g. "api/objects/recipes").
    /// </summary>
    public StubHttpMessageHandler OnPath(string path, string json)
    {
        _routes[path.TrimStart('/')] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        };
        return this;
    }

    /// <summary>
    /// Registers a handler factory for the given <paramref name="path"/>, allowing the response
    /// to vary based on the incoming request (e.g. to return different bodies per recipe id).
    /// </summary>
    public StubHttpMessageHandler OnPath(string path, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _routes[path.TrimStart('/')] = factory;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;

        // Try exact match first, then prefix match for wildcard-style paths.
        if (_routes.TryGetValue(pathAndQuery, out var exact))
            return Task.FromResult(exact(request));

        // Try matching just the path (without query string).
        var pathOnly = request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty;
        if (_routes.TryGetValue(pathOnly, out var byPath))
            return Task.FromResult(byPath(request));

        // Check prefix routes — useful for per-id endpoints like api/userfields/recipes/10.
        foreach (var (prefix, factory) in _routes)
        {
            if (pathOnly.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(factory(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"StubHttpMessageHandler: no route registered for '{pathAndQuery}'")
        });
    }
}
