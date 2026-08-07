using System.Diagnostics;
using Plantry.Ai.Infrastructure;

namespace Plantry.Tests.Unit.TestSupport;

/// <summary>
/// Subscribes an <see cref="ActivityListener"/> to the shared "Plantry.AI" source and captures the spans
/// it emits, optionally filtered to a single operation name. Filtering by operation name isolates
/// assertions from other adapters' spans on the same process-global source under xUnit's parallel class
/// scheduling. Caller owns disposal via the <c>out listener</c> parameter.
/// </summary>
internal static class AiSpanCapture
{
    public static List<Activity> Capture(string? operationName, out ActivityListener listener)
    {
        var captured = new List<Activity>();
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AiTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (operationName is null || a.OperationName == operationName)
                    captured.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return captured;
    }
}
