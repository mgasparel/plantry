using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Domain operation metrics: plantry.intake.sessions_committed,
                    // plantry.inventory.stock_consumed, plantry.inventory.low_stock_events,
                    // plantry.recipes.cooked. Emitted via DomainTelemetry counters.
                    .AddMeter("Plantry.Domain")
                    // AI pipeline metrics: ai.parse.confidence histogram (receipt parse confidence
                    // scores per line — high=1.0, low=0.5, none=0.0). Emitted by GeminiReceiptParser
                    // via AiTelemetry.ParseConfidence.
                    .AddMeter("Plantry.AI");
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation()
                    // EF Core DB spans: command duration + db.statement. db.statement carries
                    // the generated SQL with parameter placeholders only ($1, @p0) — EF does not
                    // inline literal parameter values unless EnableSensitiveDataLogging is set
                    // (it is not). Raw parameter values would additionally require the experimental
                    // env var OTEL_DOTNET_EXPERIMENTAL_EFCORE_ENABLE_TRACE_DB_QUERY_PARAMETERS,
                    // which is left unset, satisfying the no-PII-in-spans guardrail (Gate 9).
                    .AddEntityFrameworkCoreInstrumentation()
                    // Npgsql emits spans via its own ActivitySource ("Npgsql") since v5.
                    // Registering the source here causes those spans to appear nested under
                    // the EF Core command span, giving the full wire-level waterfall.
                    .AddSource("Npgsql")
                    // AI pipeline spans: receipt_parse (GeminiReceiptParser) and
                    // meal_plan_propose (MealPlannerAiService). Both carry ai.model and
                    // ai.usage.input_tokens / ai.usage.output_tokens attributes.
                    // Error-status spans + LogError fire on failure, timeout, or empty response.
                    .AddSource("Plantry.AI");
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        // /alive is a minimal liveness check used by compose healthchecks and post-deploy smoke probes;
        // it is safe to expose unconditionally (only "live"-tagged checks run, no sensitive detail).
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        // /ready probes DB connectivity via the "ready"-tagged check. Exposed unconditionally
        // (safe to expose in production) because the response writer emits ONLY "Healthy"/"Unhealthy"
        // text with the matching HTTP status code — no check names, durations, or exception detail.
        // Use for external uptime monitoring and post-deploy smoke checks. Container healthchecks
        // stay on /alive (liveness) so a DB blip does NOT mark the container unhealthy or trigger
        // restart loops for DB-independent pages.
        app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = static async (context, report) =>
            {
                // Emit only "Healthy"/"Unhealthy" — no check names, durations, or exception detail.
                // This is what makes public production exposure safe (unlike /health, which leaks all
                // check detail and stays dev-only).
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    report.Status == HealthStatus.Healthy ? "Healthy" : "Unhealthy");
            }
        });

        if (app.Environment.IsDevelopment())
        {
            // /health exposes all checks including readiness detail — keep it internal/dev-only.
            app.MapHealthChecks("/health");
        }

        return app;
    }
}
