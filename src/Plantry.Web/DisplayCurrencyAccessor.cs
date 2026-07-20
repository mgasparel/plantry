using Plantry.Identity.Application;

namespace Plantry.Web;

/// <summary>
/// Per-request cache over <see cref="IDisplayCurrency"/> (plantry-2x6e.2). A single request commonly renders
/// money in several partials and out-of-band fragments (the meal-plan page alone re-emits the plan bar, rail,
/// grid cells and rollup); routing them all through one scoped accessor resolves the household display currency
/// once — a single DB read — instead of once per call site. Registered scoped, so one instance (and one cached
/// value) lives for the lifetime of the request.
/// </summary>
/// <remarks>
/// Page models resolve the code once (e.g. in their load path) and surface it on their view models, so views
/// read the resolved string from their model rather than service-locating <see cref="IDisplayCurrency"/> from
/// Razor.
/// </remarks>
public sealed class DisplayCurrencyAccessor(IDisplayCurrency source)
{
    private string? _cached;

    /// <summary>
    /// The current household's display currency (ISO 4217, upper-case), resolved once per request and cached.
    /// Falls back to <see cref="DisplayCurrencyService.Default"/> ("USD") via the underlying service when there
    /// is no household in context or no persisted row.
    /// </summary>
    public async ValueTask<string> GetAsync(CancellationToken ct = default) =>
        _cached ??= await source.GetAsync(ct);
}
