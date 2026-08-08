using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Pantry.Application;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Registers a null <see cref="IWasteJournalReader"/> for the Today-page WAF factories (plantry-h9z9).
/// <c>IndexModel</c> now consumes <c>TodayStatsService</c> for the "did you know" stats widget, which
/// depends on <see cref="IWasteJournalReader"/> — every Today factory must supply an in-memory fake for
/// it, otherwise the real Postgres-backed <c>WasteJournalReader</c> would be resolved and hit a database
/// the L4 harness does not stand up. Mirrors <see cref="TodayDealsStubs"/>'s shape and rationale exactly
/// (same "one more real-service dependency IndexModel now has" problem, same fix).
/// </summary>
internal static class TodayWasteStatsStubs
{
    public static void RegisterEmpty(IServiceCollection services)
    {
        services.RemoveAll<IWasteJournalReader>();
        services.AddSingleton<IWasteJournalReader>(new NullWasteJournalReader());
    }
}

/// <summary>Reports zero discards ever — the always-safe default for Today L4 fixtures that don't
/// specifically exercise the stats widget's waste data.</summary>
public sealed class NullWasteJournalReader : IWasteJournalReader
{
    public Task<int> CountDiscardedSinceAsync(DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<DateTimeOffset?> MostRecentDiscardAsync(CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(null);
}
