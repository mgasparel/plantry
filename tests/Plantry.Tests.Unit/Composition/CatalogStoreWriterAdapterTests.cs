using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Deals;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="CatalogStoreWriterAdapter"/> (plantry-riqy) — the Deals→Catalog ACL adapter
/// that ensures a <c>catalog.store</c> identity for a subscribed merchant via Catalog's own
/// <see cref="Plantry.Catalog.Application.EnsureStoreCommand"/>. Covers the create-on-miss happy path, the
/// external-ref reuse path, and the failure-maps-to-exception contract (the full P5-1 idempotent
/// reuse/adopt/reactivate matrix is already proven against the command itself by <c>StoreCommandsTests</c> —
/// here we only pin the adapter's forwarding).
/// </summary>
public sealed class CatalogStoreWriterAdapterTests
{
    private static readonly Guid Household = Guid.NewGuid();

    [Fact(DisplayName = "EnsureAsync creates and returns a new store id when no store matches")]
    public async Task EnsureAsync_Creates_New_Store()
    {
        var repo = new FakeStoreRepository();
        var adapter = new CatalogStoreWriterAdapter(repo, new FakeTenantContext(Household), SystemClock.Instance);

        var id = await adapter.EnsureAsync("flipp-123", "Metro", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        var stored = Assert.Single(repo.Items);
        Assert.Equal("Metro", stored.Name);
        Assert.Equal("flipp-123", stored.ExternalRef);
    }

    [Fact(DisplayName = "EnsureAsync re-uses the existing store id when the external_ref already matches")]
    public async Task EnsureAsync_Reuses_Existing_Store_By_ExternalRef()
    {
        var existing = Plantry.Catalog.Domain.Store.Create(
            Plantry.SharedKernel.HouseholdId.From(Household), "Metro", SystemClock.Instance, "flipp-123");
        var repo = new FakeStoreRepository();
        repo.Items.Add(existing);
        var adapter = new CatalogStoreWriterAdapter(repo, new FakeTenantContext(Household), SystemClock.Instance);

        var id = await adapter.EnsureAsync("flipp-123", "Metro", CancellationToken.None);

        Assert.Equal(existing.Id.Value, id);
        Assert.Single(repo.Items);
    }

    [Fact(DisplayName = "EnsureAsync throws InvalidOperationException when the underlying command fails (no household)")]
    public async Task EnsureAsync_Throws_On_Command_Failure()
    {
        var adapter = new CatalogStoreWriterAdapter(new FakeStoreRepository(), new FakeTenantContext(null), SystemClock.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.EnsureAsync("flipp-123", "Metro", CancellationToken.None));

        Assert.Contains("Ensure catalog store failed", ex.Message);
    }
}
