namespace Plantry.Web.Dev;

/// <summary>
/// The /Dev component gallery is a development aid, not a product surface — it must never be
/// reachable outside Development, regardless of how a deployed environment is configured.
/// </summary>
public sealed class DevPagesGateMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!env.IsDevelopment() && context.Request.Path.StartsWithSegments("/Dev"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class DevPagesGateMiddlewareExtensions
{
    public static IApplicationBuilder UseDevPagesGate(this IApplicationBuilder app) =>
        app.UseMiddleware<DevPagesGateMiddleware>();
}
