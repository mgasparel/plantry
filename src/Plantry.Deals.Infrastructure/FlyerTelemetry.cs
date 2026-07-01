using System.Diagnostics;

namespace Plantry.Deals.Infrastructure;

/// <summary>
/// Telemetry primitives for the Deals context's fragile external seam (Flipp). Mirrors
/// <c>AiTelemetry</c> (Intake): each pull/directory call is wrapped in an <see cref="Activity"/> span so
/// the most-likely-to-fail, latency-sensitive network hop is individually traceable (Gate 9).
/// <para>
/// <strong>Guard:</strong> no PII or secrets are captured — spans carry only the store ref (a merchant
/// slug, not household location) and result counts. The postal code is never attached to a span or log.
/// </para>
/// Register in <c>ServiceDefaults.ConfigureOpenTelemetry</c> via <c>AddSource(FlyerTelemetry.SourceName)</c>.
/// </summary>
public static class FlyerTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name; also the name registered in ServiceDefaults tracing.</summary>
    public const string SourceName = "Plantry.Deals";

    /// <summary>Emits one span per Flipp directory search / flyer pull.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
}
