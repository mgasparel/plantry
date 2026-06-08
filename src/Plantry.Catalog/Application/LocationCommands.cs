using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Catalog.Application;

public sealed class CreateLocationCommand(
    string name,
    LocationType type,
    ILocationRepository locations,
    ITenantContext tenant)
{
    public async Task<Result<LocationId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (await locations.FindByNameAsync(name.Trim(), ct) is not null)
            return Error.Custom("Catalog.DuplicateLocationName", $"A location named '{name}' already exists.");

        var location = Location.Create(HouseholdId.From(householdId), name, type);
        await locations.AddAsync(location, ct);
        await locations.SaveChangesAsync(ct);

        return location.Id;
    }
}

public sealed class UpdateLocationCommand(
    LocationId id,
    string name,
    ILocationRepository locations)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var location = await locations.FindAsync(id, ct);
        if (location is null) return Error.NotFound;

        var existing = await locations.FindByNameAsync(name.Trim(), ct);
        if (existing is not null && existing.Id != id)
            return Error.Custom("Catalog.DuplicateLocationName", $"A location named '{name}' already exists.");

        location.Rename(name);
        await locations.SaveChangesAsync(ct);

        return Result.Success();
    }
}

/// <summary>
/// Soft-deletes a location (Gate 6). Reference data is archived, never hard-deleted, so products
/// holding the (FK-less) <c>default_location_id</c> keep resolving to a name.
/// </summary>
public sealed class ArchiveLocationCommand(LocationId id, ILocationRepository locations, IClock clock)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var location = await locations.FindAsync(id, ct);
        if (location is null) return Error.NotFound;

        location.Archive(clock);
        await locations.SaveChangesAsync(ct);

        return Result.Success();
    }
}

public sealed class UnarchiveLocationCommand(LocationId id, ILocationRepository locations)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var location = await locations.FindAsync(id, ct);
        if (location is null) return Error.NotFound;

        location.Unarchive();
        await locations.SaveChangesAsync(ct);

        return Result.Success();
    }
}
