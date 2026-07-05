using Microsoft.Extensions.Logging;
using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.Intake.Application;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Intake;

/// <summary>
/// Web-side adapter for <see cref="IEnsurePurchaseStorePort"/> — resolves a receipt merchant to a
/// <c>catalog.store</c> identity during intake commit via Catalog's own <see cref="EnsureStoreByNameCommand"/>
/// (no reimplementation; idempotent find-or-create, P5-1/DM-16). Throws on failure so the per-line commit
/// aborts that line cleanly (keeping the session resumable), mirroring the Intake create/price adapters.
/// Intake never touches <c>CatalogDbContext</c> directly (ADR-010).
/// </summary>
public sealed class EnsurePurchaseStoreAdapter(
    IStoreRepository stores,
    ITenantContext tenant,
    IClock clock,
    ILogger<EnsureStoreByNameCommand>? logger = null) : IEnsurePurchaseStorePort
{
    public async Task<Guid> EnsureAsync(string merchantName, CancellationToken ct = default)
    {
        var result = await new EnsureStoreByNameCommand(merchantName, stores, tenant, clock, logger)
            .ExecuteAsync(ct);

        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Ensure purchase store failed ({result.Error.Code}): {result.Error.Description}");

        return result.Value.Value;
    }
}
