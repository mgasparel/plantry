namespace Plantry.Identity.Application;

/// <summary>
/// The single point of truth for the current household's display currency (plantry-2x6e.1) — the ISO
/// 4217 code freshly-written money adopts and the presentation edge labels bare-decimal money with.
///
/// Budget write paths resolve the code through THIS rather than hardcoding "USD", so a household that
/// switched currency stamps the new code on its next budget save. Reads fall back to
/// <see cref="DisplayCurrencyService.Default"/> ("USD") when there is no household in context or no
/// persisted row. The write path (the /Settings/Currency picker) lives on
/// <see cref="DisplayCurrencyService"/>.
/// </summary>
public interface IDisplayCurrency
{
    /// <summary>
    /// The current household's display currency (ISO 4217, upper-case). Returns
    /// <see cref="DisplayCurrencyService.Default"/> ("USD") when there is no household in context or no
    /// persisted household row yet.
    /// </summary>
    Task<string> GetAsync(CancellationToken ct = default);
}
