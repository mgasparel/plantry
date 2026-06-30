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

    // ── AddItemCommand — reconcile rule (plantry-wxho) ────────────────────────

    [Fact(DisplayName = "AddItem — product already on list unchecked: reconciles (tops up to shortfall), no new item")]
    public async Task AddItem_ProductAlreadyUnchecked_ReconcilesToShortfall()
    {
        var (repo, list) = SeedList();

        // First add: 2 units of product1 (e.g. manual add)
        var first = await AddProduct(repo, _product1, qty: 2m).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(2m, list.Items[0].Quantity);

        // Second add: shortfall is 5 units — tops up from 2 to 5 (adds 3), not stacks to 7.
        var second = await AddProduct(repo, _product1, qty: 5m).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);                        // still one item
        Assert.Equal(5m, list.Items[0].Quantity);         // topped up to the shortfall
        Assert.Equal(first.Value, second.Value);          // same item ID returned
    }

    [Fact(DisplayName = "AddItem — same product added twice with identical shortfall: idempotent (no-op on second add)")]
    public async Task AddItem_SameShortfallTwice_IsIdempotent()
    {
        var (repo, list) = SeedList();

        // First add: shortfall of 3 units
        var first = await AddProduct(repo, _product1, qty: 3m).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(3m, list.Items[0].Quantity);

        // Second add: same shortfall of 3 units — no change, already covered
        var second = await AddProduct(repo, _product1, qty: 3m).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(3m, list.Items[0].Quantity);         // unchanged — idempotent
        Assert.Equal(first.Value, second.Value);          // same item ID returned
    }

    [Fact(DisplayName = "AddItem — shortfall smaller than amount already on list: no-op (list not reduced)")]
    public async Task AddItem_ShortfallSmallerThanExisting_IsNoOp()
    {
        var (repo, list) = SeedList();

        // First add: 5 units already on list
        var first = await AddProduct(repo, _product1, qty: 5m).ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Incoming shortfall is only 2 — already satisfied, no change
        var second = await AddProduct(repo, _product1, qty: 2m).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(5m, list.Items[0].Quantity);         // not reduced — list stays at 5
        Assert.Equal(first.Value, second.Value);
    }

    [Fact(DisplayName = "AddItem — manual 2 on list + recipe shortfall of 5 yields 5, not 7 (top-up model)")]
    public async Task AddItem_ManualPlusRecipeShortfall_TopsUpToShortfall()
    {
        var (repo, list) = SeedList();

        // Manual add: 2 units
        var first = await AddProduct(repo, _product1, qty: 2m).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(2m, list.Items[0].Quantity);

        // Recipe shortfall: 5 units needed — list should top up to 5 (toAdd = 5-2 = 3)
        var second = await AddProduct(repo, _product1, qty: 5m).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(5m, list.Items[0].Quantity); // 5, not 7
        Assert.Equal(first.Value, second.Value);
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

    [Fact(DisplayName = "AddItem — same units, incoming shortfall larger: tops up to the shortfall")]
    public async Task AddItem_SameUnits_TopsUpToShortfall()
    {
        var (repo, list) = SeedList();
        var unitA = Guid.CreateVersion7();

        // First add: 2 × unitA
        var first = await AddProduct(repo, _product1, qty: 2m, unitId: unitA).ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Second add: shortfall is 5 × unitA — tops up to 5 (toAdd = 5-2 = 3)
        var second = await AddProduct(repo, _product1, qty: 5m, unitId: unitA).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(5m, list.Items[0].Quantity);   // topped up to the shortfall
        Assert.Equal(unitA, list.Items[0].UnitId);
        Assert.Equal(first.Value, second.Value); // same item returned
    }

    [Fact(DisplayName = "AddItem — same units, same shortfall twice: idempotent (no quantity change)")]
    public async Task AddItem_SameUnits_SameShortfallTwice_IsIdempotent()
    {
        var (repo, list) = SeedList();
        var unitA = Guid.CreateVersion7();

        // First add: 3 × unitA
        var first = await AddProduct(repo, _product1, qty: 3m, unitId: unitA).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(3m, list.Items[0].Quantity);

        // Second add: same shortfall of 3 — already covered, no-op
        var second = await AddProduct(repo, _product1, qty: 3m, unitId: unitA).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(3m, list.Items[0].Quantity);   // unchanged
        Assert.Equal(first.Value, second.Value);
    }

    [Fact(DisplayName = "AddItem — convertible unit mismatch: reconciles in existing unit (tops up to converted shortfall)")]
    public async Task AddItem_ConvertibleMismatch_ReconcilesToConvertedShortfall()
    {
        var (repo, list) = SeedList();
        var unitKg = Guid.CreateVersion7(); // existing unit
        var unitG = Guid.CreateVersion7();  // incoming unit

        // Register: 2000 g of product1 → 2.0 kg
        var catalogReader = new FakeShoppingCatalogReader();
        catalogReader.RegisterConversion(fromUnitId: unitG, toUnitId: unitKg, productId: _product1, convertedAmount: 2.0m);

        // First add: 1 kg (existing item in kg)
        var first = await AddProduct(repo, _product1, qty: 1m, unitId: unitKg, catalogReader: catalogReader).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(1m, list.Items[0].Quantity);

        // Second add: 2000 g shortfall — converts to 2.0 kg; toAdd = max(0, 2.0-1.0) = 1.0 kg
        var second = await AddProduct(repo, _product1, qty: 2000m, unitId: unitG, catalogReader: catalogReader).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);                    // still one item
        Assert.Equal(2.0m, list.Items[0].Quantity);   // topped up to 2 kg (1 + 1 toAdd)
        Assert.Equal(unitKg, list.Items[0].UnitId);   // expressed in existing (kg) unit
        Assert.Equal(first.Value, second.Value);      // same item ID
    }

    [Fact(DisplayName = "AddItem — convertible unit mismatch, shortfall already covered: no-op")]
    public async Task AddItem_ConvertibleMismatch_ShortfallAlreadyCovered_IsNoOp()
    {
        var (repo, list) = SeedList();
        var unitKg = Guid.CreateVersion7();
        var unitG = Guid.CreateVersion7();

        // Register: 500 g → 0.5 kg
        var catalogReader = new FakeShoppingCatalogReader();
        catalogReader.RegisterConversion(fromUnitId: unitG, toUnitId: unitKg, productId: _product1, convertedAmount: 0.5m);

        // First add: 2 kg already on list
        var first = await AddProduct(repo, _product1, qty: 2m, unitId: unitKg, catalogReader: catalogReader).ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Second add: 500 g shortfall → converts to 0.5 kg; toAdd = max(0, 0.5-2.0) = 0 → no-op
        var second = await AddProduct(repo, _product1, qty: 500m, unitId: unitG, catalogReader: catalogReader).ExecuteAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(2m, list.Items[0].Quantity);   // unchanged
        Assert.Equal(first.Value, second.Value);
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

    // ── EditQuantityCommand (plantry-dem) ─────────────────────────────────────

    [Fact(DisplayName = "EditQuantity — sets quantity and unitId on the item")]
    public async Task EditQuantity_SetsQuantityAndUnit()
    {
        var (repo, list) = SeedList();
        var addResult = await AddProduct(repo, _product1, qty: 1m).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);
        var newUnitId = Guid.CreateVersion7();

        var cmd = new EditQuantityCommand(list.Id, addResult.Value, quantity: 5m, unitId: newUnitId, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(5m, item.Quantity);
        Assert.Equal(newUnitId, item.UnitId);
    }

    [Fact(DisplayName = "EditQuantity — unknown item ID returns NotFound")]
    public async Task EditQuantity_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var cmd = new EditQuantityCommand(list.Id, ShoppingListItemId.New(), quantity: 1m, unitId: null, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "EditQuantity — unknown list ID returns NotFound")]
    public async Task EditQuantity_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var cmd = new EditQuantityCommand(ShoppingListId.New(), ShoppingListItemId.New(), quantity: 1m, unitId: null, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "EditQuantity — no household in context returns Unauthorized")]
    public async Task EditQuantity_NoTenant_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();

        var cmd = new EditQuantityCommand(list.Id, ShoppingListItemId.New(), quantity: 1m, unitId: null, repo, Clock, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact(DisplayName = "EditQuantity — wrong household returns Unauthorized")]
    public async Task EditQuantity_WrongHousehold_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();
        var addResult = await AddProduct(repo, _product1, qty: 1m).ExecuteAsync();
        Assert.True(addResult.IsSuccess);

        // Different household tenant
        var cmd = new EditQuantityCommand(list.Id, addResult.Value, quantity: 5m, unitId: null, repo, Clock, new FakeTenantContext(Guid.NewGuid()));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── SetNoteCommand (plantry-dem) ──────────────────────────────────────────

    [Fact(DisplayName = "SetNote — sets the note on the item")]
    public async Task SetNote_SetsNote()
    {
        var (repo, list) = SeedList();
        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);

        var cmd = new SetNoteCommand(list.Id, addResult.Value, note: "organic only", repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("organic only", item.Note);
    }

    [Fact(DisplayName = "SetNote — null note clears the note field")]
    public async Task SetNote_NullNote_ClearsNote()
    {
        var (repo, list) = SeedList();
        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);
        // Set a note first
        await new SetNoteCommand(list.Id, addResult.Value, note: "first note", repo, Clock, new FakeTenantContext(_household)).ExecuteAsync();
        Assert.NotNull(item.Note);

        // Now clear it
        var result = await new SetNoteCommand(list.Id, addResult.Value, note: null, repo, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(item.Note);
    }

    [Fact(DisplayName = "SetNote — unknown item ID returns NotFound")]
    public async Task SetNote_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var cmd = new SetNoteCommand(list.Id, ShoppingListItemId.New(), note: "x", repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "SetNote — unknown list ID returns NotFound")]
    public async Task SetNote_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var cmd = new SetNoteCommand(ShoppingListId.New(), ShoppingListItemId.New(), note: "x", repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "SetNote — no household in context returns Unauthorized")]
    public async Task SetNote_NoTenant_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();

        var cmd = new SetNoteCommand(list.Id, ShoppingListItemId.New(), note: "x", repo, Clock, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact(DisplayName = "SetNote — wrong household returns Unauthorized")]
    public async Task SetNote_WrongHousehold_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();
        var addResult = await AddProduct(repo, _product1).ExecuteAsync();
        Assert.True(addResult.IsSuccess);

        // Different household tenant
        var cmd = new SetNoteCommand(list.Id, addResult.Value, note: "x", repo, Clock, new FakeTenantContext(Guid.NewGuid()));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── SetCategoryCommand (plantry-259) ──────────────────────────────────────

    [Fact(DisplayName = "SetCategory — assigns categoryId to a free-text item")]
    public async Task SetCategory_AssignsCategoryId()
    {
        var (repo, list) = SeedList();
        // Use AddFreeText to create the item
        var addResult = await AddFreeText(repo, "Sourdough").ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);
        var categoryId = Guid.CreateVersion7();

        var cmd = new SetCategoryCommand(list.Id, addResult.Value, categoryId, repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(categoryId, item.CategoryId);
    }

    [Fact(DisplayName = "SetCategory — null categoryId clears the assignment")]
    public async Task SetCategory_NullCategoryId_ClearsAssignment()
    {
        var (repo, list) = SeedList();
        var addResult = await AddFreeText(repo, "Sourdough").ExecuteAsync();
        Assert.True(addResult.IsSuccess);
        var item = list.Items.Single(i => i.Id == addResult.Value);
        var categoryId = Guid.CreateVersion7();
        // Assign first
        await new SetCategoryCommand(list.Id, addResult.Value, categoryId, repo, Clock, new FakeTenantContext(_household)).ExecuteAsync();
        Assert.NotNull(item.CategoryId);

        // Now clear it
        var result = await new SetCategoryCommand(list.Id, addResult.Value, null, repo, Clock, new FakeTenantContext(_household)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(item.CategoryId);
    }

    [Fact(DisplayName = "SetCategory — unknown item ID returns NotFound")]
    public async Task SetCategory_UnknownItem_ReturnsNotFound()
    {
        var (repo, list) = SeedList();

        var cmd = new SetCategoryCommand(list.Id, ShoppingListItemId.New(), Guid.CreateVersion7(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "SetCategory — unknown list ID returns NotFound")]
    public async Task SetCategory_UnknownList_ReturnsNotFound()
    {
        var (repo, _) = SeedList();

        var cmd = new SetCategoryCommand(ShoppingListId.New(), ShoppingListItemId.New(), Guid.CreateVersion7(), repo, Clock, new FakeTenantContext(_household));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "SetCategory — no household in context returns Unauthorized")]
    public async Task SetCategory_NoTenant_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();

        var cmd = new SetCategoryCommand(list.Id, ShoppingListItemId.New(), Guid.CreateVersion7(), repo, Clock, new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact(DisplayName = "SetCategory — wrong household returns Unauthorized")]
    public async Task SetCategory_WrongHousehold_ReturnsUnauthorized()
    {
        var (repo, list) = SeedList();
        var addResult = await AddFreeText(repo, "Sourdough").ExecuteAsync();
        Assert.True(addResult.IsSuccess);

        var cmd = new SetCategoryCommand(list.Id, addResult.Value, Guid.CreateVersion7(), repo, Clock, new FakeTenantContext(Guid.NewGuid()));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    // ── AddItemCommand — per-source contribution model (plantry-9scq) ─────────

    private AddItemCommand AddProductWithSource(
        FakeShoppingListRepository repo,
        Guid productId,
        ItemSource source,
        Guid? sourceRef,
        decimal? qty = 1m,
        Guid? unitId = null,
        FakeShoppingCatalogReader? catalogReader = null) =>
        new(productId, null, qty, unitId ?? _unitId, null,
            source, sourceRef, false,
            repo, catalogReader ?? new FakeShoppingCatalogReader(), Clock, new FakeTenantContext(_household));

    [Fact(DisplayName = "AddItem — distinct (Source, SourceRef): both contributions added; quantity is SUM")]
    public async Task AddItem_DistinctSourceKey_SumsContributions()
    {
        var (repo, list) = SeedList();
        var recipeId = Guid.CreateVersion7();

        // Manual add: 2 units
        var firstResult = await AddProductWithSource(repo, _product1, ItemSource.Manual, null, qty: 2m, unitId: _unitId).ExecuteAsync();
        Assert.True(firstResult.IsSuccess);
        Assert.Single(list.Items);
        Assert.Equal(2m, list.Items[0].Quantity);

        // Recipe add (distinct source key): 3 units — different (Source, SourceRef) pair
        var secondResult = await AddProductWithSource(repo, _product1, ItemSource.Recipe, recipeId, qty: 3m, unitId: _unitId).ExecuteAsync();
        Assert.True(secondResult.IsSuccess);

        Assert.Single(list.Items);                        // still ONE row
        Assert.Equal(5m, list.Items[0].Quantity);          // 2 + 3 = 5 (SUM of contributions)
        Assert.Equal(2, list.Items[0].Contributions.Count); // both contributions retained
        Assert.Equal(firstResult.Value, secondResult.Value);  // same item ID returned
    }

    [Fact(DisplayName = "AddItem — same (Source, SourceRef) re-add is idempotent: contribution topped up, no stacking")]
    public async Task AddItem_SameSourceKey_IdempotentTopUp()
    {
        var (repo, list) = SeedList();
        var recipeId = Guid.CreateVersion7();

        // Recipe add: 3 units
        var first = await AddProductWithSource(repo, _product1, ItemSource.Recipe, recipeId, qty: 3m, unitId: _unitId).ExecuteAsync();
        Assert.True(first.IsSuccess);
        Assert.Equal(3m, list.Items[0].Quantity);

        // Same (Recipe, recipeId) re-add with same quantity — idempotent (delta=0)
        var second = await AddProductWithSource(repo, _product1, ItemSource.Recipe, recipeId, qty: 3m, unitId: _unitId).ExecuteAsync();
        Assert.True(second.IsSuccess);

        Assert.Single(list.Items);
        Assert.Equal(3m, list.Items[0].Quantity);          // unchanged — not doubled
        Assert.Single(list.Items[0].Contributions);        // still one contribution
    }

    [Fact(DisplayName = "AddItem — two distinct SourceRefs (meal-plan slots) for same Source type SUM and do not collapse")]
    public async Task AddItem_TwoMealPlanSlots_SameProduct_SumNotCollapse()
    {
        var (repo, list) = SeedList();
        var slotMon = Guid.CreateVersion7();
        var slotThu = Guid.CreateVersion7();

        // Slot Mon: MealPlan, 2 units
        var first = await AddProductWithSource(repo, _product1, ItemSource.MealPlan, slotMon, qty: 2m, unitId: _unitId).ExecuteAsync();
        Assert.True(first.IsSuccess);

        // Slot Thu: MealPlan, 3 units — distinct SourceRef → separate contribution
        var second = await AddProductWithSource(repo, _product1, ItemSource.MealPlan, slotThu, qty: 3m, unitId: _unitId).ExecuteAsync();
        Assert.True(second.IsSuccess);

        Assert.Single(list.Items);                         // ONE row
        Assert.Equal(5m, list.Items[0].Quantity);           // 2 + 3 = 5 (summed, not collapsed)
        Assert.Equal(2, list.Items[0].Contributions.Count); // two contributions (Mon + Thu)
        Assert.Contains(list.Items[0].Contributions, c => c.SourceRef == slotMon && c.Quantity == 2m);
        Assert.Contains(list.Items[0].Contributions, c => c.SourceRef == slotThu && c.Quantity == 3m);
    }

    [Fact(DisplayName = "AddItem — same MealPlan slot re-run is idempotent (no duplicate)")]
    public async Task AddItem_SameMealPlanSlot_Idempotent()
    {
        var (repo, list) = SeedList();
        var slotId = Guid.CreateVersion7();

        var first = await AddProductWithSource(repo, _product1, ItemSource.MealPlan, slotId, qty: 1.5m, unitId: _unitId).ExecuteAsync();
        Assert.True(first.IsSuccess);

        var second = await AddProductWithSource(repo, _product1, ItemSource.MealPlan, slotId, qty: 1.5m, unitId: _unitId).ExecuteAsync();
        Assert.True(second.IsSuccess);

        Assert.Single(list.Items);
        Assert.Equal(1.5m, list.Items[0].Quantity);  // not doubled
        Assert.Single(list.Items[0].Contributions);  // one contribution (same slot)
    }
}
