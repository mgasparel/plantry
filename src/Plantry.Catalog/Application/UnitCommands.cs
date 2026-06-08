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
    ITenantContext tenant)
{
    public async Task<Result<UnitId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (await units.FindByCodeAsync(code.Trim(), ct) is not null)
            return Error.Custom("Catalog.DuplicateUnitCode", $"A unit with code '{code}' already exists.");

        var unit = Unit.Create(HouseholdId.From(householdId), code, name, dimension, factorToBase, isBase);
        await units.AddAsync(unit, ct);
        await units.SaveChangesAsync(ct);

        return unit.Id;
    }
}
