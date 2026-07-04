using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Domain;

/// <summary>
/// L1 unit tests for <see cref="ShoppingList"/> and <see cref="ShoppingListItem"/> domain behaviour.
/// Covers: item exactly-one-of product/free-text invariant; check-off sets checked_at/by;
/// clear removes only checked items (shopping.md acceptance criteria).
/// </summary>
public sealed class ShoppingListTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private readonly StubClock _clock = new();

    // ── Factory ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Create produces a list with name 'Shopping List' and no items")]
    public void Create_Sets_DefaultName_And_EmptyItems()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Equal("Shopping List", list.Name);
        Assert.Equal(Household, list.HouseholdId);
        Assert.Empty(list.Items);
        Assert.Equal(_clock.UtcNow, list.CreatedAt);
        Assert.Equal(_clock.UtcNow, list.UpdatedAt);
    }

    // ── AddItem (catalog product) ─────────────────────────────────────────

    [Fact(DisplayName = "AddItem (product) sets ProductId, leaves FreeText null; contribution carries source")]
    public void AddItem_Product_Sets_ProductId_And_Nulls_FreeText()
    {
        var list = ShoppingList.Create(Household, _clock);

        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        Assert.Equal(ProductA, item.ProductId);
        Assert.Null(item.FreeText);
        // Source/SourceRef live on the contribution (plantry-9scq).
        var contrib = Assert.Single(item.Contributions);
        Assert.Equal(ItemSource.Manual, contrib.Source);
        Assert.Single(list.Items);
    }

    // ── AddFreeTextItem ───────────────────────────────────────────────────

    [Fact(DisplayName = "AddFreeTextItem sets FreeText, leaves ProductId null; contribution is Manual")]
    public void AddFreeTextItem_Sets_FreeText_And_Nulls_ProductId()
    {
        var list = ShoppingList.Create(Household, _clock);

        var item = list.AddFreeTextItem("oat milk", quantity: null, unitId: null, note: null, _clock);

        Assert.Null(item.ProductId);
        Assert.Equal("oat milk", item.FreeText);
        // Free-text items always carry one Manual contribution (plantry-9scq).
        var contrib = Assert.Single(item.Contributions);
        Assert.Equal(ItemSource.Manual, contrib.Source);
    }

    [Fact(DisplayName = "AddFreeTextItem rejects blank or whitespace-only text")]
    public void AddFreeTextItem_Rejects_Blank_FreeText()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<ArgumentException>(() =>
            list.AddFreeTextItem("   ", quantity: null, unitId: null, note: null, _clock));
    }

    // ── CheckOff ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "CheckOff sets checked_at and checked_by on the target item")]
    public void CheckOff_Sets_CheckedAt_And_CheckedBy()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);

        _clock.Advance(TimeSpan.FromMinutes(5));
        list.CheckOff(item.Id, UserId, _clock);

        Assert.Equal(_clock.UtcNow, item.CheckedAt);
        Assert.Equal(UserId, item.CheckedBy);
        Assert.True(item.IsChecked);
    }

    [Fact(DisplayName = "CheckOff on an unknown itemId throws InvalidOperationException")]
    public void CheckOff_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.CheckOff(ShoppingListItemId.New(), UserId, _clock));
    }

    [Fact(DisplayName = "CheckOff does not affect unchecked sibling items")]
    public void CheckOff_Does_Not_Affect_Other_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        var itemB = list.AddItem(ProductB, null, null, null, ItemSource.Manual, null, _clock);

        list.CheckOff(itemA.Id, UserId, _clock);

        Assert.True(itemA.IsChecked);
        Assert.False(itemB.IsChecked);
    }

    // ── UncheckItem ──────────────────────────────────────────────────────

    [Fact(DisplayName = "UncheckItem clears checked_at and checked_by, making IsChecked false")]
    public void UncheckItem_Clears_CheckedAt_And_CheckedBy()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        list.CheckOff(item.Id, UserId, _clock);
        Assert.True(item.IsChecked);

        _clock.Advance(TimeSpan.FromMinutes(1));
        list.UncheckItem(item.Id, _clock);

        Assert.False(item.IsChecked);
        Assert.Null(item.CheckedAt);
        Assert.Null(item.CheckedBy);
    }

    [Fact(DisplayName = "UncheckItem on an unknown itemId throws InvalidOperationException")]
    public void UncheckItem_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.UncheckItem(ShoppingListItemId.New(), _clock));
    }

    [Fact(DisplayName = "UncheckItem is idempotent when item is already unchecked")]
    public void UncheckItem_AlreadyUnchecked_IsNoop()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        Assert.False(item.IsChecked);

        // Should not throw; item stays unchecked
        list.UncheckItem(item.Id, _clock);

        Assert.False(item.IsChecked);
        Assert.Null(item.CheckedAt);
    }

    // ── RemoveItem ───────────────────────────────────────────────────────

    [Fact(DisplayName = "RemoveItem hard-deletes the item from the list")]
    public void RemoveItem_Removes_Item_From_List()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        Assert.Single(list.Items);

        list.RemoveItem(item.Id, _clock);

        Assert.Empty(list.Items);
    }

    [Fact(DisplayName = "RemoveItem removes a checked item")]
    public void RemoveItem_Removes_Checked_Item()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        list.CheckOff(item.Id, UserId, _clock);

        list.RemoveItem(item.Id, _clock);

        Assert.Empty(list.Items);
    }

    [Fact(DisplayName = "RemoveItem does not affect sibling items")]
    public void RemoveItem_Does_Not_Affect_Sibling_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        var itemB = list.AddItem(ProductB, null, null, null, ItemSource.Manual, null, _clock);

        list.RemoveItem(itemA.Id, _clock);

        Assert.Single(list.Items);
        Assert.Equal(itemB.Id, list.Items[0].Id);
    }

    [Fact(DisplayName = "RemoveItem on an unknown itemId throws InvalidOperationException")]
    public void RemoveItem_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.RemoveItem(ShoppingListItemId.New(), _clock));
    }

    // ── ClearChecked ─────────────────────────────────────────────────────

    [Fact(DisplayName = "ClearChecked hard-removes only checked items, leaving unchecked intact")]
    public void ClearChecked_Removes_Only_Checked_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        var itemB = list.AddItem(ProductB, null, null, null, ItemSource.Manual, null, _clock);

        list.CheckOff(itemA.Id, UserId, _clock);
        var cleared = list.ClearChecked(_clock);

        Assert.Single(cleared);
        Assert.Equal(itemA.Id, cleared[0].Id);
        Assert.Single(list.Items);
        Assert.Equal(itemB.Id, list.Items[0].Id);
    }

    [Fact(DisplayName = "ClearChecked with no checked items returns empty list and leaves Items unchanged")]
    public void ClearChecked_NoCheckedItems_IsNoop()
    {
        var list = ShoppingList.Create(Household, _clock);
        list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);

        var cleared = list.ClearChecked(_clock);

        Assert.Empty(cleared);
        Assert.Single(list.Items);
    }

    // ── FindUncheckedByProduct (merge primitive for P2-Sb) ───────────────

    [Fact(DisplayName = "FindUncheckedByProduct returns the unchecked item when present")]
    public void FindUncheckedByProduct_Returns_UncheckedItem()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);

        var found = list.FindUncheckedByProduct(ProductA);

        Assert.NotNull(found);
        Assert.Equal(item.Id, found!.Id);
    }

    [Fact(DisplayName = "FindUncheckedByProduct returns null when the item is checked")]
    public void FindUncheckedByProduct_Returns_Null_When_Checked()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);
        list.CheckOff(item.Id, UserId, _clock);

        var found = list.FindUncheckedByProduct(ProductA);

        Assert.Null(found);
    }

    // ── EditItemQuantity (plantry-dem) ───────────────────────────────────

    [Fact(DisplayName = "EditItemQuantity sets Quantity and UnitId on the target item")]
    public void EditItemQuantity_Sets_Quantity_And_UnitId()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 1m, unitId: null, note: null, ItemSource.Manual, null, _clock);
        var newUnitId = Guid.CreateVersion7();

        _clock.Advance(TimeSpan.FromMinutes(1));
        list.EditItemQuantity(item.Id, quantity: 3.5m, unitId: newUnitId, _clock);

        Assert.Equal(3.5m, item.Quantity);
        Assert.Equal(newUnitId, item.UnitId);
        Assert.Equal(_clock.UtcNow, item.UpdatedAt);
    }

    [Fact(DisplayName = "EditItemQuantity can clear quantity (set to null)")]
    public void EditItemQuantity_Can_Clear_Quantity()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null, ItemSource.Manual, null, _clock);

        list.EditItemQuantity(item.Id, quantity: null, unitId: null, _clock);

        Assert.Null(item.Quantity);
        Assert.Null(item.UnitId);
    }

    [Fact(DisplayName = "EditItemQuantity on an unknown itemId throws InvalidOperationException")]
    public void EditItemQuantity_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.EditItemQuantity(ShoppingListItemId.New(), quantity: 1m, unitId: null, _clock));
    }

    [Fact(DisplayName = "EditItemQuantity does not affect sibling items")]
    public void EditItemQuantity_Does_Not_Affect_Sibling_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddItem(ProductA, quantity: 1m, unitId: null, note: null, ItemSource.Manual, null, _clock);
        var itemB = list.AddItem(ProductB, quantity: 5m, unitId: null, note: null, ItemSource.Manual, null, _clock);

        list.EditItemQuantity(itemA.Id, quantity: 9m, unitId: null, _clock);

        Assert.Equal(9m, itemA.Quantity);
        Assert.Equal(5m, itemB.Quantity);
    }

    // ── SetItemNote (plantry-dem) ─────────────────────────────────────────

    [Fact(DisplayName = "SetItemNote sets the note text on the target item")]
    public void SetItemNote_Sets_Note()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);

        _clock.Advance(TimeSpan.FromMinutes(1));
        list.SetItemNote(item.Id, "get the organic kind", _clock);

        Assert.Equal("get the organic kind", item.Note);
        Assert.Equal(_clock.UtcNow, item.UpdatedAt);
    }

    [Fact(DisplayName = "SetItemNote trims whitespace from the note")]
    public void SetItemNote_Trims_Whitespace()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, null, ItemSource.Manual, null, _clock);

        list.SetItemNote(item.Id, "  organic  ", _clock);

        Assert.Equal("organic", item.Note);
    }

    [Theory(DisplayName = "SetItemNote with null or whitespace-only clears the note")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetItemNote_ClearsNote_When_NullOrWhitespace(string? noteInput)
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, null, null, note: "old note", ItemSource.Manual, null, _clock);

        list.SetItemNote(item.Id, noteInput, _clock);

        Assert.Null(item.Note);
    }

    [Fact(DisplayName = "SetItemNote on an unknown itemId throws InvalidOperationException")]
    public void SetItemNote_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.SetItemNote(ShoppingListItemId.New(), "note", _clock));
    }

    [Fact(DisplayName = "SetItemNote does not affect sibling items")]
    public void SetItemNote_Does_Not_Affect_Sibling_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddItem(ProductA, null, null, note: "original A", ItemSource.Manual, null, _clock);
        var itemB = list.AddItem(ProductB, null, null, note: "original B", ItemSource.Manual, null, _clock);

        list.SetItemNote(itemA.Id, "updated A", _clock);

        Assert.Equal("updated A", itemA.Note);
        Assert.Equal("original B", itemB.Note);
    }

    // ── SetItemCategory (plantry-259) ─────────────────────────────────────

    [Fact(DisplayName = "SetItemCategory assigns the categoryId to the target item")]
    public void SetItemCategory_Sets_CategoryId()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddFreeTextItem("Sourdough", quantity: null, unitId: null, note: null, _clock);
        var categoryId = Guid.CreateVersion7();

        _clock.Advance(TimeSpan.FromMinutes(1));
        list.SetItemCategory(item.Id, categoryId, _clock);

        Assert.Equal(categoryId, item.CategoryId);
        Assert.Equal(_clock.UtcNow, item.UpdatedAt);
    }

    [Fact(DisplayName = "SetItemCategory can clear the category by passing null")]
    public void SetItemCategory_CanClear_CategoryId()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddFreeTextItem("Sourdough", quantity: null, unitId: null, note: null, _clock);
        var categoryId = Guid.CreateVersion7();
        list.SetItemCategory(item.Id, categoryId, _clock);
        Assert.NotNull(item.CategoryId);

        list.SetItemCategory(item.Id, null, _clock);

        Assert.Null(item.CategoryId);
    }

    [Fact(DisplayName = "SetItemCategory on an unknown itemId throws InvalidOperationException")]
    public void SetItemCategory_Unknown_Item_Throws()
    {
        var list = ShoppingList.Create(Household, _clock);

        Assert.Throws<InvalidOperationException>(() =>
            list.SetItemCategory(ShoppingListItemId.New(), Guid.CreateVersion7(), _clock));
    }

    [Fact(DisplayName = "SetItemCategory does not affect sibling items")]
    public void SetItemCategory_Does_Not_Affect_Sibling_Items()
    {
        var list = ShoppingList.Create(Household, _clock);
        var itemA = list.AddFreeTextItem("Bread", quantity: null, unitId: null, note: null, _clock);
        var itemB = list.AddFreeTextItem("Milk", quantity: null, unitId: null, note: null, _clock);
        var categoryId = Guid.CreateVersion7();

        list.SetItemCategory(itemA.Id, categoryId, _clock);

        Assert.Equal(categoryId, itemA.CategoryId);
        Assert.Null(itemB.CategoryId);
    }

    // ── ItemSource enum helpers ───────────────────────────────────────────

    [Theory(DisplayName = "ItemSourceExtensions round-trips all source values")]
    [InlineData(ItemSource.Manual,   "manual")]
    [InlineData(ItemSource.Recipe,   "recipe")]
    [InlineData(ItemSource.MealPlan, "meal_plan")]
    [InlineData(ItemSource.Deal,     "deal")]
    public void ItemSource_RoundTrips(ItemSource source, string dbValue)
    {
        Assert.Equal(dbValue, source.ToDbValue());
        Assert.Equal(source, ItemSourceExtensions.Parse(dbValue));
    }

    // ── Per-source contribution model (plantry-9scq) ─────────────────────

    [Fact(DisplayName = "AddItem — two distinct sources on same product → one row, quantity is SUM, both contributions retained")]
    public void AddItem_TwoDistinctSources_OneSummedRow()
    {
        var recipeId = Guid.CreateVersion7();
        var list = ShoppingList.Create(Household, _clock);

        // First add: Manual, 2 units
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        // Second add: Recipe (distinct source) — finds the existing unchecked item and upserts
        list.UpsertContribution(item, ItemSource.Recipe, recipeId, incomingQuantity: 3m, incomingUnitId: null, _clock);

        Assert.Single(list.Items);                // still ONE row
        Assert.Equal(5m, item.Quantity);           // 2 + 3 = 5 (SUM of contributions)
        Assert.Equal(2, item.Contributions.Count); // both contributions retained
        Assert.Contains(item.Contributions, c => c.Source == ItemSource.Manual && c.Quantity == 2m);
        Assert.Contains(item.Contributions, c => c.Source == ItemSource.Recipe && c.SourceRef == recipeId && c.Quantity == 3m);
    }

    [Fact(DisplayName = "UpsertContribution — same (Source, SourceRef) re-add tops up that source idempotently, no stacking")]
    public void UpsertContribution_SameKey_TopsUpIdempotently()
    {
        var recipeId = Guid.CreateVersion7();
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 3m, unitId: null, note: null,
            source: ItemSource.Recipe, sourceRef: recipeId, _clock);

        Assert.Single(item.Contributions);
        Assert.Equal(3m, item.Quantity);

        // Re-add same recipe/sourceRef with same quantity — idempotent (delta = 0)
        list.UpsertContribution(item, ItemSource.Recipe, recipeId, incomingQuantity: 3m, incomingUnitId: null, _clock);

        Assert.Single(item.Contributions);          // still ONE contribution
        Assert.Equal(3m, item.Quantity);            // unchanged — not doubled to 6
    }

    [Fact(DisplayName = "UpsertContribution — same (Source, SourceRef) with larger incoming tops up to shortfall")]
    public void UpsertContribution_SameKey_LargerShortfall_TopsUp()
    {
        var recipeId = Guid.CreateVersion7();
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Recipe, sourceRef: recipeId, _clock);

        // Same recipe, shortfall grows to 5 — top up by 3
        list.UpsertContribution(item, ItemSource.Recipe, recipeId, incomingQuantity: 5m, incomingUnitId: null, _clock);

        Assert.Single(item.Contributions);
        Assert.Equal(5m, item.Quantity);            // topped up to 5, not stacked to 7
    }

    [Fact(DisplayName = "UpsertContribution — two distinct SourceRefs for same source type (meal-plan style) SUM and do not collapse")]
    public void UpsertContribution_TwoDistinctSourceRefs_SameSource_BothSummed()
    {
        // Mon entry and Thu entry for the same recipe — they MUST be separate contributions
        var slotMon = Guid.CreateVersion7();
        var slotThu = Guid.CreateVersion7();
        var list = ShoppingList.Create(Household, _clock);

        // First add: MealPlan/slotMon, 2 units (e.g. Mon dinner for 2 servings)
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.MealPlan, sourceRef: slotMon, _clock);

        // Second add: MealPlan/slotThu — DISTINCT sourceRef → new contribution, not a top-up of Mon
        list.UpsertContribution(item, ItemSource.MealPlan, slotThu, incomingQuantity: 3m, incomingUnitId: null, _clock);

        Assert.Single(list.Items);                  // still one row
        Assert.Equal(5m, item.Quantity);             // 2 + 3 = 5 (both slot contributions sum)
        Assert.Equal(2, item.Contributions.Count);   // two separate contributions
        Assert.Contains(item.Contributions, c => c.SourceRef == slotMon && c.Quantity == 2m);
        Assert.Contains(item.Contributions, c => c.SourceRef == slotThu && c.Quantity == 3m);
    }

    [Fact(DisplayName = "UpsertContribution — same SourceRef re-add is idempotent (meal-plan same slot)")]
    public void UpsertContribution_SameMealPlanSlot_IsIdempotent()
    {
        var slotId = Guid.CreateVersion7();
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 1.5m, unitId: null, note: null,
            source: ItemSource.MealPlan, sourceRef: slotId, _clock);

        // Same meal plan slot re-runs — idempotent
        list.UpsertContribution(item, ItemSource.MealPlan, slotId, incomingQuantity: 1.5m, incomingUnitId: null, _clock);

        Assert.Single(item.Contributions);
        Assert.Equal(1.5m, item.Quantity);           // not doubled to 3.0
    }

    [Fact(DisplayName = "AddFreeTextItem — always inserts new item with single Manual contribution")]
    public void AddFreeTextItem_AlwaysInserts_WithOneManualContribution()
    {
        var list = ShoppingList.Create(Household, _clock);

        var item1 = list.AddFreeTextItem("oat milk", quantity: 1m, unitId: null, note: null, _clock);
        var item2 = list.AddFreeTextItem("oat milk", quantity: 1m, unitId: null, note: null, _clock);

        Assert.Equal(2, list.Items.Count);           // free-text items are never merged
        var c1 = Assert.Single(item1.Contributions);
        Assert.Equal(ItemSource.Manual, c1.Source);
        Assert.Null(c1.SourceRef);
        Assert.Equal(1m, c1.Quantity);
    }

    [Fact(DisplayName = "Quantity — computed as SUM of all contributions; null when no contribution has quantity")]
    public void Quantity_IsDerivedSum()
    {
        var list = ShoppingList.Create(Household, _clock);
        var recipeA = Guid.CreateVersion7();
        var recipeB = Guid.CreateVersion7();

        // Start with no quantity set (null)
        var item = list.AddItem(ProductA, quantity: null, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        Assert.Null(item.Quantity); // null when all contributions have null quantity

        // Add a Recipe contribution with 2
        list.UpsertContribution(item, ItemSource.Recipe, recipeA, incomingQuantity: 2m, incomingUnitId: null, _clock);
        Assert.Equal(2m, item.Quantity);

        // Add another Recipe contribution (distinct sourceRef) with 3
        list.UpsertContribution(item, ItemSource.Recipe, recipeB, incomingQuantity: 3m, incomingUnitId: null, _clock);
        Assert.Equal(5m, item.Quantity); // 2 + 3 = 5 (but Manual/null contribution has null qty → treated as 0)
    }

    [Fact(DisplayName = "EditQuantity — replaces Manual contribution directly, does not stack")]
    public void EditQuantity_ReplacesManuaContribution()
    {
        var list = ShoppingList.Create(Household, _clock);
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        list.EditItemQuantity(item.Id, quantity: 5m, unitId: null, _clock);

        // Should replace the 2 with 5, not stack to 7
        Assert.Equal(5m, item.Quantity);
        Assert.Single(item.Contributions);
    }

    // ── SetSourceContribution (SET/sync verb, plantry-gsj) ────────────────────

    [Fact(DisplayName = "SetSourceContribution — creates the source's slice when absent (Created)")]
    public void SetSourceContribution_Absent_CreatesSlice()
    {
        var list = ShoppingList.Create(Household, _clock);
        var recipe = Guid.CreateVersion7();
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        var change = list.SetSourceContribution(item, ItemSource.Recipe, recipe, quantity: 3m, incomingUnitId: null, _clock);

        Assert.Equal(ContributionChange.Created, change);
        // Manual 2 + Recipe 3 = 5 (sums across sources preserved, plantry-9scq).
        Assert.Equal(5m, item.Quantity);
    }

    [Fact(DisplayName = "SetSourceContribution — REPLACES (not increments) the slice; re-set to same value is Unchanged")]
    public void SetSourceContribution_Existing_Replaces_NoDrift()
    {
        var list = ShoppingList.Create(Household, _clock);
        var recipe = Guid.CreateVersion7();
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Recipe, sourceRef: recipe, _clock);

        // Set to 5 (grew), then set to 5 again (no drift), then set down to 1.
        Assert.Equal(ContributionChange.Increased, list.SetSourceContribution(item, ItemSource.Recipe, recipe, 5m, null, _clock));
        Assert.Equal(5m, item.Quantity);
        Assert.Equal(ContributionChange.Unchanged, list.SetSourceContribution(item, ItemSource.Recipe, recipe, 5m, null, _clock));
        Assert.Equal(5m, item.Quantity);
        Assert.Equal(ContributionChange.Reduced, list.SetSourceContribution(item, ItemSource.Recipe, recipe, 1m, null, _clock));
        Assert.Equal(1m, item.Quantity);
        // Still exactly one contribution — SET never appends.
        Assert.Single(item.Contributions);
    }

    // ── RemoveSourceContribution (reconcile-remove, plantry-gsj) ───────────────

    [Fact(DisplayName = "RemoveSourceContribution — a manual slice on the same row survives removing the recipe slice")]
    public void RemoveSourceContribution_KeepsRowWithOtherSource()
    {
        var list = ShoppingList.Create(Household, _clock);
        var recipe = Guid.CreateVersion7();
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);
        list.SetSourceContribution(item, ItemSource.Recipe, recipe, 3m, null, _clock);

        var removed = list.RemoveSourceContribution(item, ItemSource.Recipe, recipe, _clock);

        Assert.True(removed);
        Assert.Single(list.Items);              // row survives — manual slice keeps it alive
        Assert.Equal(2m, item.Quantity);        // only the manual 2 remains
        Assert.Single(item.Contributions);
    }

    [Fact(DisplayName = "RemoveSourceContribution — a row left with no contributions is deleted from the list")]
    public void RemoveSourceContribution_EmptyRow_DeletesItem()
    {
        var list = ShoppingList.Create(Household, _clock);
        var recipe = Guid.CreateVersion7();
        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Recipe, sourceRef: recipe, _clock);

        var removed = list.RemoveSourceContribution(item, ItemSource.Recipe, recipe, _clock);

        Assert.True(removed);
        Assert.Empty(list.Items);               // only slice removed → row deleted
    }
}

/// <summary>Controllable clock for unit tests.</summary>
internal sealed class StubClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        start ?? new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    public StubClock Advance(TimeSpan by)
    {
        UtcNow = UtcNow.Add(by);
        return this;
    }
}
