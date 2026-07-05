using Microsoft.Extensions.Logging;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Deals.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Deals;

/// <summary>
/// Web-side adapter for <see cref="ICatalogStoreWriter"/> — ensures a <c>catalog.store</c> identity for a
/// subscribed merchant via Catalog's own <see cref="EnsureStoreCommand"/> (no reimplementation; idempotent
/// reuse/adopt/reactivate, P5-1). Throws on failure so <c>ManageSubscriptions</c> aborts the subscribe,
/// mirroring the Intake/Recipes create adapters. Deals never touches <c>CatalogDbContext</c> directly.
/// </summary>
public sealed class CatalogStoreWriterAdapter(
    IStoreRepository stores,
    ITenantContext tenant,
    IClock clock,
    ILogger<EnsureStoreCommand>? logger = null) : ICatalogStoreWriter
{
    public async Task<Guid> EnsureAsync(string externalRef, string name, CancellationToken ct = default)
    {
        var result = await new EnsureStoreCommand(externalRef, name, stores, tenant, clock, logger)
            .ExecuteAsync(ct);

        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Ensure catalog store failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
