using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Application;

/// <summary>
/// L2 unit tests for AddItemCommand, CheckOffCommand, and ClearCheckedCommand.
/// Uses in-memory doubles — no database required.
/// Covers: merge vs. intentional-dup, exactly-one-of constraint, check-off/clear lifecycle,
/// and unit-mismatch merge policy (plantry-xw6).
/// </summary>
public sealed class ShoppingCommandsTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _product1 = Guid.CreateVersion7();
    private readonly Guid _product2 = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (FakeShoppingListRepository repo, ShoppingList list) SeedList()
    {
        var repo = new FakeShoppingListRepository();
        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        repo.Seed(list);
        return (repo, list);
    }

    private AddItemCommand AddProduct(
        FakeShoppingListRepository repo,
        Guid productId,
        decimal? qty = 1m,
        bool intentionalDuplicate = false,
        Guid? unitId = null,
        FakeShoppingCatalogReader? catalogReader = null) =>
        new(productId, null, qty, unitId ?? _unitId, null,
            ItemSource.Manual, null, intentionalDuplicate,
            repo, catalogReader ?? new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household));

    private AddItemCommand AddFreeText(
        FakeShoppingListRepository repo,
        string text,
        decimal? qty = null) =>
        new(null, text, qty, null, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household));

    private CheckOffCommand CheckOff(
        FakeShoppingListRepository repo,
        ShoppingListId listId,
        ShoppingListItemId itemId) =>
        new(listId, itemId, _userId, repo, Clock, new FakeTenantContext(_household));

    private ClearCheckedCommand ClearChecked(FakeShoppingListRepository repo) =>
        new(repo, Clock, new FakeTenantContext(_household));

    // ── AddItemCommand — merge rule ───────────────────────────────────────────

    [Fact(DisplayName = "AddItem — product already on list unchecked: merges quantity, no new item")]
    public async Task AddItem_ProductAlreadyUnchecked_MergesQuantity()
    {
        var (repo, list) = SeedList();

        // First add: 2 units of product1
        var first = await AddProduct(repo, _product1, qty: 2m).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(2m, list.Items[0].Quantity);

        // Second add: 3 units of product1 — should MERGE, not insert
        var second = await AddProduct(repo, _product1, qty: 3m).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);                        // still one item
        Assert.Equal(5m, list.Items[0].Quantity);         // incremented
        Assert.Equal(first.Value, second.Value);          // same item ID returned
    }

    [Fact(DisplayName = "AddItem — intentional-dup flag bypasses merge and inserts second line")]
    public async Task AddItem_IntentionalDuplicate_InsertsSecondLine()
    {
        var (repo, list) = SeedList();

        await AddProduct(repo, _product1, qty: 2m).ExecuteAsync();
        Assert.Single(list.Items);

        var second = await AddProduct(repo, _product1, qty: 1m, intentionalDuplicate: true).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Equal(2, list.Items.Count);  // intentional second line added
    }

    [Fact(DisplayName = "AddItem — checked item for same product does NOT trigger merge; new item inserted")]
    public async Task AddItem_ProductChecked_InsertsNewItem()
    {
        var (repo, list) = SeedList();

        // Add and check off product1
        var firstResult = await AddProduct(repo, _product1, qty: 1m).ExecuteAsync();
        Assert.True(firstResult.IsSuccess);
        list.CheckOff(firstResult.Value, _userId, Clock);

        // Add product1 again — checked item should not merge
        var secondResult = await AddProduct(repo, _product1, qty: 1m).ExecuteAsync();

        Assert.True(secondResult.IsSuccess);
        Assert.Equal(2, list.Items.Count);
        Assert.NotEqual(firstResult.Value, secondResult.Value);
    }

    [Fact(DisplayName = "AddItem — free-text item with no product is created successfully")]
    public async Task AddItem_FreeText_IsAdded()
    {
        var (repo, list) = SeedList();

        var result = await AddFreeText(repo, "Sourdough bread", qty: 1m).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var item = Assert.Single(list.Items);
        Assert.Null(item.ProductId);
        Assert.Equal("Sourdough bread", item.FreeText);
    }

    [Fact(DisplayName = "AddItem — both productId and freeText supplied returns exactly-one-of error")]
    public async Task AddItem_BothProductAndFreeText_ReturnsError()
    {
        var (repo, _) = SeedList();

        var cmd = new AddItemCommand(
            Guid.CreateVersion7(), "also text", 1m, null, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household));

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Shopping.InvalidItem", result.Error.Code);
    }

    [Fact(DisplayName = "AddItem — neither productId nor freeText supplied returns exactly-one-of error")]
    public async Task AddItem_NeitherProductNorFreeText_ReturnsError()
    {
        var (repo, _) = SeedList();

        var cmd = new AddItemCommand(
            null, null, 1m, null, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household));

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Shopping.InvalidItem", result.Error.Code);
    }

    [Fact(DisplayName = "AddItem — no household in context returns Unauthorized")]
    public async Task AddItem_NoTenant_ReturnsUnauthorized()
    {
        var (repo, _) = SeedList();

        var cmd = new AddItemCommand(
            _product1, null, 1m, null, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(null));

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── CheckOffCommand ───────────────────────────────────────────────────────

    [Fact(DisplayName = "CheckOff — stamps checked_at and checked_by on the item")]
    public async Task CheckOff_StampsCheckedFields()
    {
        var (repo, list) = SeedList();

        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);
        Assert.False(item.IsChecked);

        var result = await CheckOff(repo, list.Id, addResult.Value).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.True(item.IsChecked);
        Assert.NotNull(item.CheckedAt);
        Assert.Equal(_userId, item.CheckedBy);
    }

    [Fact(DisplayName = "CheckOff — unknown item ID returns NotFound")]
    public async Task CheckOff_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var result = await CheckOff(repo, list.Id, ShoppingListItemId.New()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "CheckOff — unknown list ID returns NotFound")]
    public async Task CheckOff_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var result = await CheckOff(repo, ShoppingListId.New(), ShoppingListItemId.New()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    // ── ClearCheckedCommand ───────────────────────────────────────────────────

    [Fact(DisplayName = "ClearChecked — removes only checked items; unchecked items remain")]
    public async Task ClearChecked_RemovesOnlyCheckedItems()
    {
        var (repo, list) = SeedList();

        var p1Result = await AddProduct(repo, _product1).ExecuteAsync();
        var p2Result = await AddProduct(repo, _product2).ExecuteAsync();
        Assert.True(p1Result.IsSuccess && p2Result.IsSuccess);

        // Check off product1 only
        await CheckOff(repo, list.Id, p1Result.Value).ExecuteAsync();
        Assert.Equal(2, list.Items.Count);

        var clearResult = await ClearChecked(repo).ExecuteAsync();

        Assert.True(clearResult.IsSuccess);
        Assert.Equal(1, clearResult.Value);    // one item cleared
        Assert.Single(list.Items);             // unchecked product2 remains
        Assert.Equal(p2Result.Value, list.Items[0].Id);
    }

    [Fact(DisplayName = "ClearChecked — with no checked items returns 0 and does not call Save")]
    public async Task ClearChecked_NoCheckedItems_ReturnsZeroWithoutSave()
    {
        var (repo, _) = SeedList();
        await AddProduct(repo, _product1).ExecuteAsync();
        var savesBeforeClear = repo.SaveCalls;

        var result = await ClearChecked(repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Equal(savesBeforeClear, repo.SaveCalls); // no extra save
    }

    [Fact(DisplayName = "ClearChecked — no household in context returns Unauthorized")]
    public async Task ClearChecked_NoTenant_ReturnsUnauthorized()
    {
        var repo = new FakeShoppingListRepository();

        var result = await new ClearCheckedCommand(repo, Clock, new FakeTenantContext(null)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── UncheckItemCommand ────────────────────────────────────────────────────

    [Fact(DisplayName = "UncheckItem — clears checked_at and checked_by")]
    public async Task UncheckItem_ClearsCheckedFields()
    {
        var (repo, list) = SeedList();

        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);

        // Check it off first
        await CheckOff(repo, list.Id, addResult.Value).ExecuteAsync();
        Assert.True(item.IsChecked);

        // Now uncheck
        var cmd = new UncheckItemCommand(list.Id, addResult.Value, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.False(item.IsChecked);
        Assert.Null(item.CheckedAt);
        Assert.Null(item.CheckedBy);
    }

    [Fact(DisplayName = "UncheckItem — unknown item ID returns NotFound")]
    public async Task UncheckItem_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var cmd = new UncheckItemCommand(list.Id, ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "UncheckItem — unknown list ID returns NotFound")]
    public async Task UncheckItem_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var cmd = new UncheckItemCommand(ShoppingListId.New(), ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "UncheckItem — no household in context returns Unauthorized")]
    public async Task UncheckItem_NoTenant_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();

        var cmd = new UncheckItemCommand(list.Id, ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── DeleteItemCommand ─────────────────────────────────────────────────────

    [Fact(DisplayName = "DeleteItem — removes the item from the list")]
    public async Task DeleteItem_RemovesItem()
    {
        var (repo, list) = SeedList();

        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        Assert.Single(list.Items);

        var cmd = new DeleteItemCommand(list.Id, addResult.Value, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(list.Items);
    }

    [Fact(DisplayName = "DeleteItem — removes a checked item")]
    public async Task DeleteItem_RemovesCheckedItem()
    {
        var (repo, list) = SeedList();

        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);

        // Check it off
        await CheckOff(repo, list.Id, addResult.Value).ExecuteAsync();
        Assert.True(list.Items.Single().IsChecked);

        var cmd = new DeleteItemCommand(list.Id, addResult.Value, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(list.Items);
    }

    [Fact(DisplayName = "DeleteItem — unknown item ID returns NotFound")]
    public async Task DeleteItem_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var cmd = new DeleteItemCommand(list.Id, ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "DeleteItem — unknown list ID returns NotFound")]
    public async Task DeleteItem_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var cmd = new DeleteItemCommand(ShoppingListId.New(), ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "DeleteItem — no household in context returns Unauthorized")]
    public async Task DeleteItem_NoTenant_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();

        var cmd = new DeleteItemCommand(list.Id, ShoppingListItemId.New(), repo, Clock, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── AddItemCommand — unit-mismatch merge policy (plantry-xw6) ────────────

    [Fact(DisplayName = "AddItem — same units: merges by summing as before (regression guard)")]
    public async Task AddItem_SameUnits_MergesBySum()
    {
        var (repo, list) = SeedList();
        var unitA = Guid.CreateVersion7();

        // First add: 2 × unitA
        var first = await AddProduct(repo, _product1, qty: 2m, unitId: unitA).ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Second add: 3 × unitA — same unit, should merge to 5
        var second = await AddProduct(repo, _product1, qty: 3m, unitId: unitA).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(5m, list.Items[0].Quantity);
        Assert.Equal(unitA, list.Items[0].UnitId);
        Assert.Equal(first.Value, second.Value); // same item returned
    }

    [Fact(DisplayName = "AddItem — convertible unit mismatch (e.g. g→kg): merges with converted total in existing unit")]
    public async Task AddItem_ConvertibleMismatch_MergesWithConvertedTotal()
    {
        var (repo, list) = SeedList();
        var unitKg = Guid.CreateVersion7(); // existing unit
        var unitG = Guid.CreateVersion7();  // incoming unit

        // Register: 500 g of product1 → 0.5 kg
        var catalogReader = new FakeShoppingCatalogReader();
        catalogReader.RegisterConversion(fromUnitId: unitG, toUnitId: unitKg, productId: _product1, convertedAmount: 0.5m);

        // First add: 1 kg (existing item in kg)
        var first = await AddProduct(repo, _product1, qty: 1m, unitId: unitKg, catalogReader: catalogReader).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);

        // Second add: 500 g — should be converted to 0.5 kg and merged → 1.5 kg total
        var second = await AddProduct(repo, _product1, qty: 500m, unitId: unitG, catalogReader: catalogReader).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);                    // still one item
        Assert.Equal(1.5m, list.Items[0].Quantity);   // 1 kg + 0.5 kg converted
        Assert.Equal(unitKg, list.Items[0].UnitId);   // expressed in existing (kg) unit
        Assert.Equal(first.Value, second.Value);      // same item ID
    }

    [Fact(DisplayName = "AddItem — unconvertible unit mismatch (cross-dimension, no product conversion): inserts second line")]
    public async Task AddItem_UnconvertibleMismatch_InsertsSecondLine()
    {
        var (repo, list) = SeedList();
        var unitMass = Guid.CreateVersion7();   // e.g. grams
        var unitVolume = Guid.CreateVersion7(); // e.g. mL — different dimension, no conversion registered

        // No conversion registered in the fake catalog reader (returns null by default).
        var catalogReader = new FakeShoppingCatalogReader();

        // First add: 100 g
        var first = await AddProduct(repo, _product1, qty: 100m, unitId: unitMass, catalogReader: catalogReader).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);

        // Second add: 200 mL — unconvertible → second line
        var second = await AddProduct(repo, _product1, qty: 200m, unitId: unitVolume, catalogReader: catalogReader).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Equal(2, list.Items.Count);            // second line inserted
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact(DisplayName = "AddItem — one side has no unit (null vs. set): inserts second line")]
    public async Task AddItem_OneSideNullUnit_InsertsSecondLine()
    {
        var (repo, list) = SeedList();
        var unitA = Guid.CreateVersion7();

        // First add: quantity with a unit
        var first = await new AddItemCommand(
            _product1, null, 1m, unitA, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household))
            .ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);

        // Second add: same product but no unit → mismatch (null vs. set) → second line
        var second = await new AddItemCommand(
            _product1, null, 2m, null, null,
            ItemSource.Manual, null, false,
            repo, new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household))
            .ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Equal(2, list.Items.Count);   // second line, not a merge
        Assert.NotEqual(first.Value, second.Value);
    }
}
