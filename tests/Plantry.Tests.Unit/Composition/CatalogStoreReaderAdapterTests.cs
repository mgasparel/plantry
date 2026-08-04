using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Catalog.Application;
using Plantry.Web.Deals;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="CatalogStoreReaderAdapter"/> (plantry-riqy) — the Deals→Catalog ACL adapter
/// that resolves <c>catalog.store</c> identities over Catalog's own <see cref="Plantry.Pantry.Domain.IStoreRepository"/>.
/// Covers the single-store lookup and the batch name-resolution, including the archived-store case
/// (ResolveNamesAsync must still resolve an unsubscribed/archived merchant's name, DM-16).
/// </summary>
public sealed class CatalogStoreReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();

    [Fact(DisplayName = "FindAsync resolves a known store to its (Id, Name, ExternalRef)")]
    public async Task FindAsync_Resolves_Known_Store()
    {
        var store = Plantry.Pantry.Domain.Store.Create(Household, "Costco", SystemClock.Instance, "costco-ext");
        var repo = new FakeStoreRepository();
        repo.Items.Add(store);

        var result = await new CatalogStoreReaderAdapter(repo).FindAsync(store.Id.Value);

        Assert.NotNull(result);
        Assert.Equal(store.Id.Value, result!.StoreId);
        Assert.Equal("Costco", result.Name);
        Assert.Equal("costco-ext", result.ExternalRef);
    }

    [Fact(DisplayName = "FindAsync returns null for an unknown store id")]
    public async Task FindAsync_Returns_Null_For_Unknown_Store()
    {
        var result = await new CatalogStoreReaderAdapter(new FakeStoreRepository()).FindAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact(DisplayName = "ResolveNamesAsync resolves names for requested ids, including archived stores")]
    public async Task ResolveNamesAsync_Resolves_Including_Archived()
    {
        var active = Plantry.Pantry.Domain.Store.Create(Household, "FreshCo", SystemClock.Instance);
        var archived = Plantry.Pantry.Domain.Store.Create(Household, "Old Merchant", SystemClock.Instance);
        archived.Archive(SystemClock.Instance);
        var repo = new FakeStoreRepository();
        repo.Items.Add(active);
        repo.Items.Add(archived);

        var result = await new CatalogStoreReaderAdapter(repo)
            .ResolveNamesAsync([active.Id.Value, archived.Id.Value]);

        Assert.Equal("FreshCo", result[active.Id.Value]);
        Assert.Equal("Old Merchant", result[archived.Id.Value]);
    }

    [Fact(DisplayName = "ResolveNamesAsync omits ids not present in Catalog")]
    public async Task ResolveNamesAsync_Omits_Unknown_Ids()
    {
        var repo = new FakeStoreRepository();
        // Decoy: a store the call does NOT ask for, so Assert.Empty can only hold if the adapter
        // filters ListAsync down to the requested ids.
        repo.Items.Add(Plantry.Pantry.Domain.Store.Create(Household, "FreshCo", SystemClock.Instance));
        var unknown = Guid.NewGuid();

        var result = await new CatalogStoreReaderAdapter(repo).ResolveNamesAsync([unknown]);

        Assert.Empty(result);
    }

    [Fact(DisplayName = "ResolveNamesAsync returns an empty map for an empty id list")]
    public async Task ResolveNamesAsync_ShortCircuits_On_Empty_Input()
    {
        var result = await new CatalogStoreReaderAdapter(new FakeStoreRepository()).ResolveNamesAsync([]);

        Assert.Empty(result);
    }
}
