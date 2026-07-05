using Microsoft.Extensions.Logging;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Identity.Application;

/// <summary>
/// Reads and writes the household-wide "AI assistance" switch (plantry-qll2.1), and serves as the
/// <see cref="IAiAssistanceGate"/> read source for every governed call site. The setting lives on the
/// <see cref="Household"/> aggregate root (like <c>Theme</c> and <c>ExpiryWarningDays</c>) — one row
/// per household in the <c>identity</c> schema, already the tenant anchor — so no separate settings
/// table or RLS wiring is needed. Defaults to <see cref="DefaultEnabled"/> (ON) when unset.
/// </summary>
public sealed class AiAssistanceSettingsService(
    IHouseholdRepository households,
    ITenantContext tenant,
    ILogger<AiAssistanceSettingsService> logger) : IAiAssistanceGate
{
    /// <summary>Fallback when there is no household in context or no persisted row — the assistive class is opt-out.</summary>
    public const bool DefaultEnabled = true;

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return DefaultEnabled;

        var household = await households.FindAsync(HouseholdId.From(householdGuid), ct);
        return household?.AiAssistanceEnabled ?? DefaultEnabled;
    }

    /// <summary>
    /// Persists the household's AI-assistance switch. Returns a failure when there is no household in
    /// context (unauthorized) or the household row cannot be found.
    /// </summary>
    public async Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
        {
            logger.LogWarning("SetAiAssistanceEnabled rejected — no household in context.");
            return Error.Unauthorized;
        }

        var householdId = HouseholdId.From(householdGuid);
        var household = await households.FindAsync(householdId, ct);
        if (household is null)
        {
            logger.LogWarning(
                "SetAiAssistanceEnabled rejected — household {HouseholdId} not found.", householdId.Value);
            return Error.NotFound;
        }

        household.SetAiAssistanceEnabled(enabled);
        await households.SaveChangesAsync(ct);
        logger.LogInformation(
            "AI assistance set to {Enabled} for household {HouseholdId}.", enabled, householdId.Value);

        return Result.Success();
    }
}
