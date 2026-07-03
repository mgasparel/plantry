using Plantry.Catalog.Application;
using Plantry.Catalog.Domain;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Application;

public sealed class StoreCommandsTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    [Fact]
    public async Task CreateStoreCommand_Adds_Manual_Store_With_Null_ExternalRef()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new CreateStoreCommand("FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var store = Assert.Single(repo.Items);
        Assert.Equal(result.Value, store.Id);
        Assert.Equal(householdId, store.HouseholdId.Value);
        Assert.Null(store.ExternalRef);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task CreateStoreCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new CreateStoreCommand("FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task CreateStoreCommand_Fails_On_Duplicate_Name()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);
        repo.Items.Add(Store.Create(Plantry.SharedKernel.HouseholdId.From(householdId), "FreshCo", Clock));

        var result = await new CreateStoreCommand("FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.DuplicateStoreName", result.Error.Code);
        Assert.Single(repo.Items);
    }

    [Fact]
    public async Task EnsureStoreCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task EnsureStoreCommand_Miss_Creates_New_Store_With_ExternalRef()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var store = Assert.Single(repo.Items);
        Assert.Equal(result.Value, store.Id);
        Assert.Equal("flipp-1", store.ExternalRef);
        Assert.Equal("FreshCo", store.Name);
    }

    [Fact]
    public async Task EnsureStoreCommand_ExternalRef_Hit_Reuses_Existing_Row()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        var existing = Store.Create(householdId, "FreshCo", Clock, "flipp-1");
        repo.Items.Add(existing);

        // A second ensure with the same external_ref but a different display name still reuses the row.
        var result = await new EnsureStoreCommand("flipp-1", "FreshCo Downtown", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
        Assert.Single(repo.Items);
    }

    [Fact]
    public async Task EnsureStoreCommand_Name_Hit_Adopts_And_BackFills_ExternalRef()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        // User manually created "FreshCo" (null external_ref) before subscribing to Flipp's "FreshCo".
        var manual = Store.Create(householdId, "FreshCo", Clock);
        repo.Items.Add(manual);

        var result = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(manual.Id, result.Value);
        var store = Assert.Single(repo.Items);
        Assert.Equal("flipp-1", store.ExternalRef);
    }

    [Fact]
    public async Task EnsureStoreCommand_Archived_ExternalRef_Match_Reactivates()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        var existing = Store.Create(householdId, "FreshCo", Clock, "flipp-1");
        existing.Archive(Clock);
        repo.Items.Add(existing);

        var result = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
        var store = Assert.Single(repo.Items);
        Assert.False(store.IsArchived);
    }

    [Fact]
    public async Task EnsureStoreCommand_Archived_Name_Match_Adopts_And_Reactivates()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        var manual = Store.Create(householdId, "FreshCo", Clock);
        manual.Archive(Clock);
        repo.Items.Add(manual);

        var result = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var store = Assert.Single(repo.Items);
        Assert.Equal(manual.Id, store.Id);
        Assert.Equal("flipp-1", store.ExternalRef);
        Assert.False(store.IsArchived);
    }

    [Fact]
    public async Task EnsureStoreCommand_Is_Idempotent_Returning_Same_Id()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var first = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();
        var second = await new EnsureStoreCommand("flipp-1", "FreshCo", repo, tenant, Clock).ExecuteAsync();

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Single(repo.Items);
    }

    // ── EnsureStoreByNameCommand (purchase-side, name-only find-or-create) ───────────────────────

    [Fact]
    public async Task EnsureStoreByNameCommand_Miss_Creates_Manual_Store_With_Null_ExternalRef()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var result = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var store = Assert.Single(repo.Items);
        Assert.Equal(result.Value, store.Id);
        Assert.Equal("Superstore", store.Name);
        Assert.Null(store.ExternalRef); // manual store — does not weaken the external_ref path
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task EnsureStoreByNameCommand_Name_Hit_Reuses_Existing_Row_Without_Creating()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        var existing = Store.Create(householdId, "Superstore", Clock);
        repo.Items.Add(existing);

        var result = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value);
        Assert.Single(repo.Items);
        Assert.Equal(0, repo.SaveChangesCalls); // pure reuse — no write
    }

    [Fact]
    public async Task EnsureStoreByNameCommand_Is_Idempotent_Returning_Same_Id()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var first = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();
        var second = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Single(repo.Items);
    }

    [Fact]
    public async Task EnsureStoreByNameCommand_Fails_When_No_Household_In_Context()
    {
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(null);

        var result = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Items);
    }

    [Fact]
    public async Task EnsureStoreByNameCommand_Reuses_Archived_Store_Without_Reactivating_It()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId.Value);
        var archived = Store.Create(householdId, "Superstore", Clock);
        archived.Archive(Clock);
        repo.Items.Add(archived);

        var result = await new EnsureStoreByNameCommand("Superstore", repo, tenant, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(archived.Id, result.Value);
        var store = Assert.Single(repo.Items);
        Assert.True(store.IsArchived); // a purchase does not resurrect an archived merchant
    }

    [Fact]
    public async Task EnsureStoreByNameCommand_Collapses_Internal_Whitespace_So_Variants_Resolve_To_One_Row()
    {
        var householdId = Guid.NewGuid();
        var repo = new FakeStoreRepository();
        var tenant = new FakeTenantContext(householdId);

        var created = await new EnsureStoreByNameCommand("Metro  Etobicoke", repo, tenant, Clock).ExecuteAsync();
        var reused = await new EnsureStoreByNameCommand("Metro Etobicoke", repo, tenant, Clock).ExecuteAsync();

        Assert.True(created.IsSuccess);
        Assert.True(reused.IsSuccess);
        Assert.Equal(created.Value, reused.Value);
        var store = Assert.Single(repo.Items);
        Assert.Equal("Metro Etobicoke", store.Name); // stored in the normalized (collapsed) form
    }

    [Fact]
    public async Task ArchiveStoreCommand_Soft_Deletes_Keeping_It_Resolvable()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var store = Store.Create(householdId, "FreshCo", Clock);
        repo.Items.Add(store);

        var result = await new ArchiveStoreCommand(store.Id, repo, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(store.IsArchived);
        var stillPresent = Assert.Single(repo.Items);
        Assert.Same(store, stillPresent);
        Assert.Empty(await repo.ListActiveAsync());
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task ArchiveStoreCommand_Fails_When_Store_Not_Found()
    {
        var repo = new FakeStoreRepository();

        var result = await new ArchiveStoreCommand(StoreId.New(), repo, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task UnarchiveStoreCommand_Restores_Archived_Store()
    {
        var householdId = Plantry.SharedKernel.HouseholdId.New();
        var repo = new FakeStoreRepository();
        var store = Store.Create(householdId, "FreshCo", Clock);
        store.Archive(Clock);
        repo.Items.Add(store);

        var result = await new UnarchiveStoreCommand(store.Id, repo, Clock).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(store.IsArchived);
        Assert.Single(await repo.ListActiveAsync());
    }

    [Fact]
    public async Task UnarchiveStoreCommand_Fails_When_Store_Not_Found()
    {
        var repo = new FakeStoreRepository();

        var result = await new UnarchiveStoreCommand(StoreId.New(), repo, Clock).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
