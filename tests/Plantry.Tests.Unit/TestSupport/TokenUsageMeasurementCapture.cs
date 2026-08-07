using System.Diagnostics.Metrics;
using Plantry.Ai.Infrastructure;

namespace Plantry.Tests.Unit.TestSupport;

/// <summary>
/// Subscribes a <see cref="MeterListener"/> to the shared "ai.usage.tokens" instrument and captures only the
/// measurements tagged with <c>model</c> — the discriminator that isolates a test from whatever other adapter
/// tests emit on the same process-global instrument under xUnit's parallel class scheduling.
/// </summary>
internal static class TokenUsageMeasurementCapture
{
    public static List<(long Value, string Function, string Kind)> Capture(
        string model, out MeterListener listener)
    {
        var measurements = new List<(long, string, string)>();
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AiTelemetry.SourceName && instrument.Name == "ai.usage.tokens")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? function = null, taggedModel = null, kind = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "ai.function") function = (string?)tag.Value;
                else if (tag.Key == "ai.model") taggedModel = (string?)tag.Value;
                else if (tag.Key == "ai.token_kind") kind = (string?)tag.Value;
            }
            if (taggedModel == model) measurements.Add((value, function!, kind!));
        });
        listener.Start();
        return measurements;
    }
}
