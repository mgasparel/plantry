using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Catalog.Application;

public sealed class CreateUnitCommand(
    string code,
    string name,
    Dimension dimension,
    decimal factorToBase,
    bool isBase,
    IUnitRepository units,
    ITenantContext tenant,
    ILogger<CreateUnitCommand>? logger = null)
{
    public async Task<Result<UnitId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (await units.FindByCodeAsync(code.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateUnit rejected — duplicate unit code {UnitCode}.", code);
            return Error.Custom("Catalog.DuplicateUnitCode", $"A unit with code '{code}' already exists.");
        }

        var unit = Unit.Create(HouseholdId.From(householdId), code, name, dimension, factorToBase, isBase);
        await units.AddAsync(unit, ct);
        await units.SaveChangesAsync(ct);

        logger?.LogInformation("Unit {UnitId} created with code {UnitCode}.", unit.Id.Value, code);
        return unit.Id;
    }
}

/// <summary>
/// Sets a unit's <see cref="DisplayStyle"/> (quantity-display.md Q2). Display-only — no stored
/// quantity or downstream calculation changes.
/// </summary>
public sealed class SetDisplayStyleCommand(
    UnitId unitId,
    DisplayStyle style,
    IUnitRepository units,
    ITenantContext tenant,
    ILogger<SetDisplayStyleCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        // The repository query filters by household (RLS + query filter), so a unit from another
        // household resolves to null here — never a cross-tenant mutation.
        if (await units.FindAsync(unitId, ct) is not { } unit)
        {
            logger?.LogWarning("SetDisplayStyle rejected — unit {UnitId} not found.", unitId.Value);
            return Error.NotFound;
        }

        unit.SetDisplayStyle(style);
        await units.SaveChangesAsync(ct);

        logger?.LogInformation("Unit {UnitId} display style set to {DisplayStyle}.", unit.Id.Value, style);
        return Result.Success();
    }
}

/// <summary>
/// Sets a unit's <see cref="UnitSystem"/> (quantity-display.md Q5) — the metric/imperial simplification
/// firewall. Display-only: no stored quantity or downstream calculation changes.
/// </summary>
public sealed class SetUnitSystemCommand(
    UnitId unitId,
    UnitSystem system,
    IUnitRepository units,
    ITenantContext tenant,
    ILogger<SetUnitSystemCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is null)
            return Error.Unauthorized;

        // The repository query filters by household (RLS + query filter), so a unit from another
        // household resolves to null here — never a cross-tenant mutation.
        if (await units.FindAsync(unitId, ct) is not { } unit)
        {
            logger?.LogWarning("SetUnitSystem rejected — unit {UnitId} not found.", unitId.Value);
            return Error.NotFound;
        }

        unit.SetUnitSystem(system);
        await units.SaveChangesAsync(ct);

        logger?.LogInformation("Unit {UnitId} unit system set to {UnitSystem}.", unit.Id.Value, system);
        return Result.Success();
    }
}
