using Microsoft.Extensions.Logging;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Identity.Application;

/// <summary>
/// Reads and writes the household display currency (plantry-2x6e.1), and serves as the
/// <see cref="IDisplayCurrency"/> read source for the money-write call sites (budget writers today,
/// the presentation edge as the epic lands). The setting lives on the <see cref="Household"/> aggregate
/// root (like <c>Theme</c> and <c>AiAssistanceEnabled</c>) — one row per household in the
/// <c>identity</c> schema, already the tenant anchor — so no separate settings table or RLS wiring is
/// needed. Falls back to <see cref="Default"/> ("USD") when unset.
/// </summary>
public sealed class DisplayCurrencyService(
    IHouseholdRepository households,
    ITenantContext tenant,
    ILogger<DisplayCurrencyService> logger) : IDisplayCurrency
{
    /// <summary>Fallback when there is no household in context or no persisted row.</summary>
    public const string Default = "USD";

    /// <inheritdoc />
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return Default;

        var household = await households.FindAsync(HouseholdId.From(householdGuid), ct);
        return household?.DisplayCurrency ?? Default;
    }

    /// <summary>
    /// Persists the household's display currency. Returns a failure when there is no household in
    /// context (unauthorized) or the household row cannot be found. The aggregate normalizes and
    /// validates the code (3-letter A–Z); an invalid code surfaces as an <see cref="ArgumentException"/>
    /// from the caller's binding, not here.
    /// </summary>
    public async Task<Result> SetAsync(string code, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetDisplayCurrency rejected — no household in context.");
            return Error.Unauthorized;
        }

        var householdId = HouseholdId.From(householdGuid);
        var household = await households.FindAsync(householdId, ct);
        if (household is null)
        {
            logger.LogWarning(
                "SetDisplayCurrency rejected — household {HouseholdId} not found.", householdId.Value);
            return Error.NotFound;
        }

        household.SetDisplayCurrency(code);
        await households.SaveChangesAsync(ct);
        logger.LogInformation(
            "Display currency set to {Currency} for household {HouseholdId}.",
            household.DisplayCurrency, householdId.Value);

        return Result.Success();
    }
}
