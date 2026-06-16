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

    [Fact(DisplayName = "AddItem (product) sets ProductId, leaves FreeText null")]
    public void AddItem_Product_Sets_ProductId_And_Nulls_FreeText()
    {
        var list = ShoppingList.Create(Household, _clock);

        var item = list.AddItem(ProductA, quantity: 2m, unitId: null, note: null,
            source: ItemSource.Manual, sourceRef: null, _clock);

        Assert.Equal(ProductA, item.ProductId);
        Assert.Null(item.FreeText);
        Assert.Equal(ItemSource.Manual, item.Source);
        Assert.Single(list.Items);
    }

    // ── AddFreeTextItem ───────────────────────────────────────────────────

    [Fact(DisplayName = "AddFreeTextItem sets FreeText, leaves ProductId null — invariant satisfied")]
    public void AddFreeTextItem_Sets_FreeText_And_Nulls_ProductId()
    {
        var list = ShoppingList.Create(Household, _clock);

        var item = list.AddFreeTextItem("oat milk", quantity: null, unitId: null, note: null, _clock);

        Assert.Null(item.ProductId);
        Assert.Equal("oat milk", item.FreeText);
        Assert.Equal(ItemSource.Manual, item.Source);
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
