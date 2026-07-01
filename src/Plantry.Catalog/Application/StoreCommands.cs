using Microsoft.Extensions.Logging;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Catalog.Application;

/// <summary>
/// Creates a manually-named merchant (null <c>external_ref</c>). Subscribing to a flyer-source
/// merchant goes through <see cref="EnsureStoreCommand"/> instead.
/// </summary>
public sealed class CreateStoreCommand(
    string name,
    IStoreRepository stores,
    ITenantContext tenant,
    IClock clock,
    ILogger<CreateStoreCommand>? logger = null)
{
    public async Task<Result<StoreId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        if (await stores.FindByNameAsync(name.Trim(), ct) is not null)
        {
            logger?.LogWarning("CreateStore rejected — duplicate store name {StoreName}.", name);
            return Error.Custom("Catalog.DuplicateStoreName", $"A store named '{name}' already exists.");
        }

        var store = Store.Create(HouseholdId.From(householdId), name, clock);
        await stores.AddAsync(store, ct);
        await stores.SaveChangesAsync(ct);

        logger?.LogInformation("Store {StoreId} created with name {StoreName}.", store.Id.Value, name);
        return store.Id;
    }
}

/// <summary>
/// Idempotent ensure-by-external-identity — the command a subscribe (P5-2) calls to reuse an
/// existing merchant row rather than mint duplicates (deals.md §store_subscription). Resolves in
/// order:
/// <list type="number">
/// <item>match on <c>external_ref</c> → reuse (reactivate if archived);</item>
/// <item>else match on <c>name</c> → adopt the existing row by back-filling <c>external_ref</c>
/// (reactivate if archived), avoiding a <c>UNIQUE (household_id, name)</c> collision;</item>
/// <item>else create.</item>
/// </list>
/// This keeps exactly one row per merchant; re-ensuring the same <c>(external_ref, name)</c> is a
/// no-op that returns the same id.
/// </summary>
public sealed class EnsureStoreCommand(
    string externalRef,
    string name,
    IStoreRepository stores,
    ITenantContext tenant,
    IClock clock,
    ILogger<EnsureStoreCommand>? logger = null)
{
    public async Task<Result<StoreId>> ExecuteAsync(CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdId)
            return Error.Unauthorized;

        ArgumentException.ThrowIfNullOrWhiteSpace(externalRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmedRef = externalRef.Trim();
        var trimmedName = name.Trim();

        // (1) External-ref hit — reuse this merchant row; re-subscribe reactivates it.
        var existing = await stores.FindByExternalRefAsync(trimmedRef, ct);
        if (existing is not null)
        {
            var wasArchived = existing.IsArchived;
            existing.Unarchive(clock);
            await stores.SaveChangesAsync(ct);
            logger?.LogInformation(
                "EnsureStore matched store {StoreId} on external_ref {ExternalRef} (reactivated: {Reactivated}).",
                existing.Id.Value, trimmedRef, wasArchived);
            return existing.Id;
        }

        // (2) Name hit — adopt the manually-created row by back-filling external_ref onto it.
        existing = await stores.FindByNameAsync(trimmedName, ct);
        if (existing is not null)
        {
            existing.AdoptExternalRef(trimmedRef, clock);
            existing.Unarchive(clock);
            await stores.SaveChangesAsync(ct);
            logger?.LogInformation(
                "EnsureStore adopted store {StoreId} by name {StoreName}, back-filling external_ref {ExternalRef}.",
                existing.Id.Value, trimmedName, trimmedRef);
            return existing.Id;
        }

        // (3) Miss — create a new merchant identity.
        var store = Store.Create(HouseholdId.From(householdId), trimmedName, clock, trimmedRef);
        await stores.AddAsync(store, ct);
        await stores.SaveChangesAsync(ct);
        logger?.LogInformation(
            "EnsureStore created store {StoreId} for external_ref {ExternalRef}.", store.Id.Value, trimmedRef);
        return store.Id;
    }
}

/// <summary>
/// Soft-deletes a store (DM-4). Reference data is archived, never hard-deleted, so price
/// observations and deals holding the (FK-less) <c>store_id</c> keep resolving to a name.
/// </summary>
public sealed class ArchiveStoreCommand(StoreId id, IStoreRepository stores, IClock clock, ILogger<ArchiveStoreCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var store = await stores.FindAsync(id, ct);
        if (store is null)
        {
            logger?.LogWarning("ArchiveStore failed — store {StoreId} not found.", id.Value);
            return Error.NotFound;
        }

        store.Archive(clock);
        await stores.SaveChangesAsync(ct);

        logger?.LogInformation("Store {StoreId} archived.", id.Value);
        return Result.Success();
    }
}

public sealed class UnarchiveStoreCommand(StoreId id, IStoreRepository stores, IClock clock, ILogger<UnarchiveStoreCommand>? logger = null)
{
    public async Task<Result> ExecuteAsync(CancellationToken ct = default)
    {
        var store = await stores.FindAsync(id, ct);
        if (store is null)
        {
            logger?.LogWarning("UnarchiveStore failed — store {StoreId} not found.", id.Value);
            return Error.NotFound;
        }

        store.Unarchive(clock);
        await stores.SaveChangesAsync(ct);

        logger?.LogInformation("Store {StoreId} unarchived.", id.Value);
        return Result.Success();
    }
}
