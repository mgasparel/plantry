using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Unit.Shopping.Application;

/// <summary>
/// L2 unit tests for <see cref="ShoppingListQueryService"/>.
/// Verifies that Note and CategoryHue round-trip through the view model
/// (plantry-77f acceptance criteria), and that pantry stock levels (on-hand / IsLow)
/// are enriched via the Shopping→Inventory ACL port (plantry-juh).
/// CategoryCode derivation is intentionally not in the read model — downstream markup
/// feeds <c>CatChipViewModel(CategoryName, CategoryHue)</c> to the shared <c>_CatChip</c>
/// partial, which owns the code derivation.
/// </summary>
public sealed class ShoppingListQueryServiceTests
{
    private static readonly IClock Clock = SystemClock.Instance;

    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _productId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ShoppingListQueryService BuildService(
        FakeShoppingListRepository repo,
        FakeShoppingCatalogReaderWithSummaries catalog,
        FakeShoppingPantryReader? pantryReader = null,
        FakeShoppingRecipeReader? recipeReader = null,
        FakeShoppingDealReader? dealReader = null,
        FakeShoppingMealPlanReader? mealPlanReader = null,
        FakeShoppingDealAttributionReader? dealAttributionReader = null,
        IClock? clock = null)
    {
        return new ShoppingListQueryService(
            repo,
            catalog,
            pantryReader ?? new FakeShoppingPantryReader(),
            recipeReader ?? new FakeShoppingRecipeReader(),
            mealPlanReader ?? new FakeShoppingMealPlanReader(),
            dealAttributionReader ?? new FakeShoppingDealAttributionReader(),
            dealReader ?? new FakeShoppingDealReader(),
            clock ?? Clock,
            new FakeTenantContext(_household));
    }

    private ShoppingList SeedListWithProductItem(
        FakeShoppingListRepository repo,
        string? note)
    {
        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 1m, unitId: _unitId, note: note,
            source: ItemSource.Manual, sourceRef: null, Clock);
        repo.Seed(list);
        return list;
    }

    private ShoppingList SeedListWithFreeTextItem(
        FakeShoppingListRepository repo,
        string? note)
    {
        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddFreeTextItem("Sourdough bread", quantity: null, unitId: null, note: note, Clock);
        repo.Seed(list);
        return list;
    }

    // ── Note round-trip ───────────────────────────────────────────────────────

    [Fact(DisplayName = "GetList — product item note persisted on domain entity surfaces on view model")]
    public async Task GetList_ProductItem_NoteRoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 60));

        SeedListWithProductItem(repo, note: "skimmed only");

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items)
            .Concat(view.UncategorizedItems)
            .Concat(view.CheckedItems));
        Assert.Equal("skimmed only", item.Note);
    }

    [Fact(DisplayName = "GetList — free-text item note surfaces on view model")]
    public async Task GetList_FreeTextItem_NoteRoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: "gluten-free if possible");

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Equal("gluten-free if possible", item.Note);
    }

    [Fact(DisplayName = "GetList — null note on domain entity produces null Note on view model")]
    public async Task GetList_NullNote_ProducesNullNoteOnView()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 60));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Null(item.Note);
    }

    // ── CategoryHue round-trip ────────────────────────────────────────────────

    [Fact(DisplayName = "GetList — CategoryHue from catalog summary threads through to view model")]
    public async Task GetList_CategoryHue_RoundTrips()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy", CategoryHue: 200));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(200, item.CategoryHue);
    }

    [Fact(DisplayName = "GetList — uncategorised product (null CategoryHue) produces null hue on view model")]
    public async Task GetList_UncategorisedProduct_ProducesNullHue()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Generic Product", null, CategoryHue: null));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        // Product with no category lands in UncategorizedItems
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Null(item.CategoryHue);
    }

    [Fact(DisplayName = "GetList — free-text item (no catalog entry) produces null CategoryHue on view model")]
    public async Task GetList_FreeTextItem_ProducesNullCategoryHue()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Null(item.CategoryHue);
    }

    // ── Combined hue + note round-trip ────────────────────────────────────────

    [Fact(DisplayName = "GetList — CategoryHue and Note both round-trip together through the view")]
    public async Task GetList_HueAndNote_BothRoundTripTogether()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Cheddar", "Dairy & Eggs", CategoryHue: 55));

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 250m, unitId: _unitId, note: "mature preferred",
            source: ItemSource.Manual, sourceRef: null, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(55, item.CategoryHue);
        Assert.Equal("mature preferred", item.Note);
    }

    // ── Pantry stock enrichment (plantry-juh) ─────────────────────────────────

    [Fact(DisplayName = "GetList — product with stock: OnHand and PantryUnitCode populated on view model")]
    public async Task GetList_ProductWithStock_OnHandAndUnitCodePopulated()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        var pantry = new FakeShoppingPantryReader();
        pantry.RegisterStock(_productId, new ShoppingPantryStockLevel(_productId, 2.5m, "L", IsLow: false));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, pantry);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(2.5m, item.OnHand);
        Assert.Equal("L", item.PantryUnitCode);
        Assert.False(item.IsLow);
        Assert.True(item.HasPantryStock);
    }

    [Fact(DisplayName = "GetList — running-low product: IsLow flag flows through to the view item")]
    public async Task GetList_RunningLowProduct_IsLowTrue()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Eggs", "Dairy"));

        var pantry = new FakeShoppingPantryReader();
        // Running low: positive but low quantity (0 < onHand ≤ threshold). IsLow is running-low
        // only now (plantry-43y) — the query service passes it straight through to the view.
        pantry.RegisterStock(_productId, new ShoppingPantryStockLevel(_productId, 1m, "ea", IsLow: true));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, pantry);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(1m, item.OnHand);
        Assert.True(item.IsLow);
        Assert.True(item.HasPantryStock);
    }

    [Fact(DisplayName = "GetList — out product: IsLow = false (out is not running low), OnHand = 0")]
    public async Task GetList_OutProduct_IsLowFalse()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Eggs", "Dairy"));

        var pantry = new FakeShoppingPantryReader();
        // Out: onHand ≤ 0 → IsLow is false (running-low only). The subline renders "out", not "low".
        pantry.RegisterStock(_productId, new ShoppingPantryStockLevel(_productId, 0m, "ea", IsLow: false));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, pantry);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(0m, item.OnHand);
        Assert.False(item.IsLow);
        Assert.True(item.HasPantryStock);
    }

    [Fact(DisplayName = "GetList — product with no inventory record: OnHand null, HasPantryStock false")]
    public async Task GetList_ProductWithNoStockRecord_OnHandNullHasPantryStockFalse()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Flour", "Baking"));

        // Pantry reader returns empty — product has never been stocked.
        var pantry = new FakeShoppingPantryReader();

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, pantry);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Null(item.OnHand);
        Assert.Null(item.PantryUnitCode);
        Assert.Null(item.IsLow);
        Assert.False(item.HasPantryStock);
    }

    [Fact(DisplayName = "GetList — free-text item: HasPantryStock false regardless of pantry reader")]
    public async Task GetList_FreeTextItem_HasPantryStockFalse()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        var pantry = new FakeShoppingPantryReader();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog, pantry);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.False(item.HasPantryStock);
        Assert.Null(item.OnHand);
    }

    // ── SetCategory / recategorize grouping (plantry-259) ────────────────────

    [Fact(DisplayName = "GetList — free-text item with CategoryId set: appears in named group, not Uncategorized")]
    public async Task GetList_FreeTextItemWithCategoryId_AppearsInNamedGroup()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        var categoryId = Guid.CreateVersion7();
        catalog.RegisterCategory(categoryId, new ShoppingCategoryOption(categoryId, "Bread & Bakery", Hue: 30));

        var list = ShoppingList.Create(HouseholdId.From(_household), SystemClock.Instance);
        var item = list.AddFreeTextItem("Sourdough", quantity: null, unitId: null, note: null, SystemClock.Instance);
        // Simulate recategorize by setting CategoryId on the domain entity directly (domain unit)
        list.SetItemCategory(item.Id, categoryId, SystemClock.Instance);
        repo.Seed(list);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        // Item should be in a named group, not UncategorizedItems
        Assert.Empty(view.UncategorizedItems);
        var group = Assert.Single(view.Groups);
        Assert.Equal("Bread & Bakery", group.CategoryName);
        var viewItem = Assert.Single(group.Items);
        Assert.Equal("Sourdough", viewItem.DisplayName);
        Assert.Equal("Bread & Bakery", viewItem.CategoryName);
        Assert.Equal(30, viewItem.CategoryHue);
    }

    [Fact(DisplayName = "GetList — free-text item without CategoryId: appears in Uncategorized")]
    public async Task GetList_FreeTextItemWithoutCategoryId_AppearsInUncategorized()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        Assert.Empty(view.Groups);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.Equal("Sourdough bread", item.DisplayName);
        Assert.Null(item.CategoryName);
    }

    // ── Attribution labels (plantry-26g) ─────────────────────────────────────

    [Fact(DisplayName = "GetList — Manual-only item: AttributionLabels contains 'added by you'")]
    public async Task GetList_ManualOnlyItem_AttributionIsAddedByYou()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasAttribution);
        Assert.Equal([new AttributionLabel(AttributionKind.Manual, "added by you")], item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — Recipe-only item: AttributionLabels contains 'for {RecipeName}'")]
    public async Task GetList_RecipeOnlyItem_AttributionForRecipe()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Chicken", "Meat"));

        var recipes = new FakeShoppingRecipeReader();
        recipes.RegisterRecipe(recipeId, "Chicken Stir Fry");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 500m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: recipeId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasAttribution);
        Assert.Equal([new AttributionLabel(AttributionKind.Recipe, "for Chicken Stir Fry")], item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — two distinct Recipe sources: AttributionLabels contains both names")]
    public async Task GetList_TwoDistinctRecipeSources_BothNamesInAttribution()
    {
        var recipeAId = Guid.CreateVersion7();
        var recipeBId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Bell Peppers", "Vegetables"));

        var recipes = new FakeShoppingRecipeReader();
        recipes.RegisterRecipe(recipeAId, "Chicken Stir Fry");
        recipes.RegisterRecipe(recipeBId, "Caesar Salad");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        // First contribution from recipe A
        var existingItem = list.AddItem(_productId, quantity: 2m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: recipeAId, Clock);
        // Second contribution from recipe B (distinct SourceRef → sums; plantry-9scq)
        list.UpsertContribution(existingItem, ItemSource.Recipe, recipeBId, incomingQuantity: 3m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasAttribution);
        // Both recipe names should appear; order is first-seen. Both are structurally Recipe.
        Assert.Equal(
            [
                new AttributionLabel(AttributionKind.Recipe, "for Chicken Stir Fry"),
                new AttributionLabel(AttributionKind.Recipe, "for Caesar Salad"),
            ],
            item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — same recipe twice (meal-plan style): AttributionLabels shows 'for {Name} ×2'")]
    public async Task GetList_SameRecipeNameTwoDistinctRefs_ShowsCountSuffix()
    {
        // Two distinct SourceRefs that both resolve to the same recipe name simulate
        // the meal-plan-repeat pattern described in the design (Mon + Thu same recipe).
        var slotARef = Guid.CreateVersion7(); // meal-plan slot A
        var slotBRef = Guid.CreateVersion7(); // meal-plan slot B
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Rice", "Grains"));

        var recipes = new FakeShoppingRecipeReader();
        // Both SourceRefs resolve to the same recipe name
        recipes.RegisterRecipe(slotARef, "Fried Rice");
        recipes.RegisterRecipe(slotBRef, "Fried Rice");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        var existingItem = list.AddItem(_productId, quantity: 200m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: slotARef, Clock);
        list.UpsertContribution(existingItem, ItemSource.Recipe, slotBRef, incomingQuantity: 200m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasAttribution);
        Assert.Equal([new AttributionLabel(AttributionKind.Recipe, "for Fried Rice ×2")], item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — mixed Recipe+Manual item: both labels appear in order")]
    public async Task GetList_MixedRecipeAndManual_BothLabelsPresent()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Olive Oil", "Oils"));

        var recipes = new FakeShoppingRecipeReader();
        recipes.RegisterRecipe(recipeId, "Pasta Primavera");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        // Start with a manual add
        var existingItem = list.AddItem(_productId, quantity: 1m, unitId: _unitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        // Then a recipe add (distinct source → adds a second contribution)
        list.UpsertContribution(existingItem, ItemSource.Recipe, recipeId, incomingQuantity: 2m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasAttribution);
        // Recipe labels appear first (kind Recipe), then manual (kind Manual).
        Assert.Equal(
            [
                new AttributionLabel(AttributionKind.Recipe, "for Pasta Primavera"),
                new AttributionLabel(AttributionKind.Manual, "added by you"),
            ],
            item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — free-text item (Manual): 'added by you' in attribution")]
    public async Task GetList_FreeTextItem_ShowsAddedByYouAttribution()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.True(item.HasAttribution);
        Assert.Equal([new AttributionLabel(AttributionKind.Manual, "added by you")], item.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — Recipe contribution with unknown recipeId: no attribution label emitted")]
    public async Task GetList_RecipeContributionUnknownRecipeId_NoAttributionLabel()
    {
        var deletedRecipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Garlic", "Produce"));

        // Recipe reader returns nothing (recipe deleted or in another household)
        var recipes = new FakeShoppingRecipeReader();

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 3m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: deletedRecipeId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        // Deleted/unknown recipe should produce no attribution (graceful degradation)
        Assert.False(item.HasAttribution);
    }

    // ── MealPlan attribution (plantry-jwyb) ──────────────────────────────────

    [Fact(DisplayName = "GetList — MealPlan item across two slots: one line per distinct slot, no ×N")]
    public async Task GetList_MealPlanTwoSlots_TwoLinesInOrder()
    {
        var slotMon = Guid.CreateVersion7();
        var slotThu = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Rice", "Grains"));

        var mealPlans = new FakeShoppingMealPlanReader();
        mealPlans.RegisterSlot(slotMon, DayOfWeek.Monday, "Dinner");
        mealPlans.RegisterSlot(slotThu, DayOfWeek.Thursday, "Dinner");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        var item = list.AddItem(_productId, quantity: 200m, unitId: _unitId, note: null,
            source: ItemSource.MealPlan, sourceRef: slotMon, Clock);
        // Distinct slot SourceRef → a second contribution that sums (ShoppingListItem.cs:163).
        list.UpsertContribution(item, ItemSource.MealPlan, slotThu, incomingQuantity: 200m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, mealPlanReader: mealPlans);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(
            [
                new AttributionLabel(AttributionKind.MealPlan, "for Mon dinner"),
                new AttributionLabel(AttributionKind.MealPlan, "for Thu dinner"),
            ],
            viewItem.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — MealPlan item with unresolved slot: falls back to 'for your meal plan'")]
    public async Task GetList_MealPlanUnresolvedSlot_FallsBack()
    {
        var unknownSlot = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Rice", "Grains"));

        // Reader resolves nothing — slot deleted, foreign, or a coarser whole-plan ref.
        var mealPlans = new FakeShoppingMealPlanReader();

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 200m, unitId: _unitId, note: null,
            source: ItemSource.MealPlan, sourceRef: unknownSlot, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, mealPlanReader: mealPlans);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(
            [new AttributionLabel(AttributionKind.MealPlan, "for your meal plan")],
            viewItem.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — MealPlan two unresolved slots collapse to a single fallback line")]
    public async Task GetList_MealPlanTwoUnresolvedSlots_SingleFallbackLine()
    {
        var slotA = Guid.CreateVersion7();
        var slotB = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Rice", "Grains"));

        var mealPlans = new FakeShoppingMealPlanReader(); // resolves neither

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        var item = list.AddItem(_productId, quantity: 100m, unitId: _unitId, note: null,
            source: ItemSource.MealPlan, sourceRef: slotA, Clock);
        list.UpsertContribution(item, ItemSource.MealPlan, slotB, incomingQuantity: 100m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, mealPlanReader: mealPlans);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        // Both unresolved refs map to the same fallback text → a single line.
        Assert.Equal(
            [new AttributionLabel(AttributionKind.MealPlan, "for your meal plan")],
            viewItem.AttributionLabels);
    }

    // ── Deal attribution (plantry-jwyb) ──────────────────────────────────────

    [Fact(DisplayName = "GetList — Deal item with resolved store: 'on sale at {store}'")]
    public async Task GetList_DealResolvedStore_ShowsStoreLabel()
    {
        var dealId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Butter", "Dairy"));

        var dealAttribution = new FakeShoppingDealAttributionReader();
        dealAttribution.RegisterDealStore(dealId, "Metro");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 1m, unitId: null, note: null,
            source: ItemSource.Deal, sourceRef: dealId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, dealAttributionReader: dealAttribution);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(
            [new AttributionLabel(AttributionKind.Deal, "on sale at Metro")],
            viewItem.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — Deal item with unresolved store: falls back to plain 'on sale'")]
    public async Task GetList_DealUnresolvedStore_FallsBack()
    {
        var dealId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Butter", "Dairy"));

        // Reader resolves nothing — deal deleted/foreign, or its store unresolved.
        var dealAttribution = new FakeShoppingDealAttributionReader();

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 1m, unitId: null, note: null,
            source: ItemSource.Deal, sourceRef: dealId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, dealAttributionReader: dealAttribution);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(
            [new AttributionLabel(AttributionKind.Deal, "on sale")],
            viewItem.AttributionLabels);
    }

    [Fact(DisplayName = "GetList — all four kinds: labels emit in order Recipe → MealPlan → Deal → Manual")]
    public async Task GetList_AllKinds_EmitInFixedOrder()
    {
        var recipeId = Guid.CreateVersion7();
        var slotRef = Guid.CreateVersion7();
        var dealId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Olive Oil", "Oils"));

        var recipes = new FakeShoppingRecipeReader();
        recipes.RegisterRecipe(recipeId, "Pasta Primavera");
        var mealPlans = new FakeShoppingMealPlanReader();
        mealPlans.RegisterSlot(slotRef, DayOfWeek.Friday, "Lunch");
        var dealAttribution = new FakeShoppingDealAttributionReader();
        dealAttribution.RegisterDealStore(dealId, "Loblaws");

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        // Seed contributions out of the final emission order (Manual first) to prove ordering is by kind.
        var item = list.AddItem(_productId, quantity: 1m, unitId: _unitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        list.UpsertContribution(item, ItemSource.Deal, dealId, incomingQuantity: 1m, incomingUnitId: _unitId, Clock);
        list.UpsertContribution(item, ItemSource.MealPlan, slotRef, incomingQuantity: 1m, incomingUnitId: _unitId, Clock);
        list.UpsertContribution(item, ItemSource.Recipe, recipeId, incomingQuantity: 1m, incomingUnitId: _unitId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog, recipeReader: recipes,
            mealPlanReader: mealPlans, dealAttributionReader: dealAttribution);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var viewItem = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.Equal(
            [
                new AttributionLabel(AttributionKind.Recipe, "for Pasta Primavera"),
                new AttributionLabel(AttributionKind.MealPlan, "for Fri lunch"),
                new AttributionLabel(AttributionKind.Deal, "on sale at Loblaws"),
                new AttributionLabel(AttributionKind.Manual, "added by you"),
            ],
            viewItem.AttributionLabels);
    }

    // ── Deal badge (P5-9, Shopping→Pricing read model) ───────────────────────

    [Fact(DisplayName = "GetList — product with an active deal: DealStoreName/DealId populated, HasDeal true")]
    public async Task GetList_ProductWithActiveDeal_DealFieldsPopulated()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        var dealId = Guid.CreateVersion7();
        var storeId = Guid.CreateVersion7();
        var deals = new FakeShoppingDealReader();
        deals.RegisterDeal(_productId, new ShoppingActiveDeal(_productId, dealId, storeId, "FreshCo"));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, dealReader: deals);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasDeal);
        Assert.Equal("FreshCo", item.DealStoreName);
        Assert.Equal(dealId, item.DealId);
    }

    [Fact(DisplayName = "GetList — product with no active deal: HasDeal false, deal fields null")]
    public async Task GetList_ProductWithNoActiveDeal_NoBadge()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        // Deal reader returns nothing — no active deal in the window (or window lapsed).
        var deals = new FakeShoppingDealReader();

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, dealReader: deals);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.False(item.HasDeal);
        Assert.Null(item.DealStoreName);
        Assert.Null(item.DealId);
    }

    [Fact(DisplayName = "GetList — active deal with unresolved store: HasDeal true, DealStoreName null (storeless badge)")]
    public async Task GetList_ActiveDealNoStore_HasDealButNullStoreName()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        var dealId = Guid.CreateVersion7();
        var deals = new FakeShoppingDealReader();
        // storeId null / name unresolved → badge falls back to "On sale this week".
        deals.RegisterDeal(_productId, new ShoppingActiveDeal(_productId, dealId, StoreId: null, StoreName: null));

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, dealReader: deals);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.Groups.SelectMany(g => g.Items));
        Assert.True(item.HasDeal);
        Assert.Null(item.DealStoreName);
        Assert.Equal(dealId, item.DealId);
    }

    [Fact(DisplayName = "GetList — free-text item: no deal lookup, HasDeal false")]
    public async Task GetList_FreeTextItem_NoDeal()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        var deals = new FakeShoppingDealReader();

        SeedListWithFreeTextItem(repo, note: null);

        var svc = BuildService(repo, catalog, dealReader: deals);
        var view = await svc.GetListAsync();

        Assert.NotNull(view);
        var item = Assert.Single(view.UncategorizedItems);
        Assert.False(item.HasDeal);
    }

    [Fact(DisplayName = "GetList — deal reader is evaluated against the clock's today (window activation/lapse)")]
    public async Task GetList_DealReader_ReceivesClockToday()
    {
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();
        catalog.RegisterSummary(_productId, new ShoppingProductSummary(_productId, "Milk", "Dairy"));

        var deals = new FakeShoppingDealReader();
        var fixedInstant = new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.Zero);
        var clock = new FixedClock(fixedInstant);

        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog, dealReader: deals, clock: clock);
        await svc.GetListAsync();

        // The query service must pass the clock's UTC date so a deal appears/lapses with its window.
        Assert.Equal(new DateOnly(2026, 7, 4), deals.LastToday);
    }

    // ── HasRecipeContribution (plantry-yt0m) ─────────────────────────────────

    [Fact(DisplayName = "HasRecipeContribution — true when the list carries a Recipe contribution for the recipe id")]
    public async Task HasRecipeContribution_RecipeContributionPresent_ReturnsTrue()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 2m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: recipeId, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog);

        Assert.True(await svc.HasRecipeContributionAsync(recipeId));
    }

    [Fact(DisplayName = "HasRecipeContribution — false when only a Manual contribution exists for the product")]
    public async Task HasRecipeContribution_OnlyManualContribution_ReturnsFalse()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        // Manual-sourced item (SourceRef null) — not a recipe contribution.
        SeedListWithProductItem(repo, note: null);

        var svc = BuildService(repo, catalog);

        Assert.False(await svc.HasRecipeContributionAsync(recipeId));
    }

    [Fact(DisplayName = "HasRecipeContribution — false when the Recipe contribution belongs to a different recipe")]
    public async Task HasRecipeContribution_DifferentRecipeId_ReturnsFalse()
    {
        var thisRecipe = Guid.CreateVersion7();
        var otherRecipe = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        var list = ShoppingList.Create(HouseholdId.From(_household), Clock);
        list.AddItem(_productId, quantity: 1m, unitId: _unitId, note: null,
            source: ItemSource.Recipe, sourceRef: otherRecipe, Clock);
        repo.Seed(list);

        var svc = BuildService(repo, catalog);

        Assert.False(await svc.HasRecipeContributionAsync(thisRecipe));
    }

    [Fact(DisplayName = "HasRecipeContribution — false when the household has no shopping list")]
    public async Task HasRecipeContribution_NoList_ReturnsFalse()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        var svc = BuildService(repo, catalog);

        Assert.False(await svc.HasRecipeContributionAsync(recipeId));
    }

    [Fact(DisplayName = "HasRecipeContribution — false when there is no household context")]
    public async Task HasRecipeContribution_NoHousehold_ReturnsFalse()
    {
        var recipeId = Guid.CreateVersion7();
        var repo = new FakeShoppingListRepository();
        var catalog = new FakeShoppingCatalogReaderWithSummaries();

        var svc = new ShoppingListQueryService(
            repo, catalog,
            new FakeShoppingPantryReader(),
            new FakeShoppingRecipeReader(),
            new FakeShoppingMealPlanReader(),
            new FakeShoppingDealAttributionReader(),
            new FakeShoppingDealReader(),
            Clock,
            new FakeTenantContext(null));

        Assert.False(await svc.HasRecipeContributionAsync(recipeId));
    }
}

/// <summary>Deterministic <see cref="IClock"/> for asserting "today" derivation in the read model.</summary>
internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

/// <summary>
/// Variant of <see cref="FakeShoppingCatalogReader"/> that supports registering
/// specific product summaries (including CategoryHue) for query service tests.
/// </summary>
internal sealed class FakeShoppingCatalogReaderWithSummaries : IShoppingCatalogReader
{
    private readonly Dictionary<Guid, ShoppingProductSummary> _summaries = [];
    private readonly Dictionary<(Guid from, Guid to, Guid product), decimal> _conversions = [];
    private readonly Dictionary<Guid, ShoppingCategoryOption> _categories = [];

    public void RegisterSummary(Guid productId, ShoppingProductSummary summary) =>
        _summaries[productId] = summary;

    public void RegisterConversion(Guid fromUnitId, Guid toUnitId, Guid productId, decimal convertedAmount) =>
        _conversions[(fromUnitId, toUnitId, productId)] = convertedAmount;

    public void RegisterCategory(Guid categoryId, ShoppingCategoryOption option) =>
        _categories[categoryId] = option;

    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ShoppingProductSummary> result = productIds
            .Where(_summaries.ContainsKey)
            .ToDictionary(id => id, id => _summaries[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingProductCandidate>>([]);

    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default)
    {
        decimal? result = _conversions.TryGetValue((fromUnitId, toUnitId, productId), out var converted)
            ? converted
            : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShoppingUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingUnitOption>>([]);

    public Task<IReadOnlyList<ShoppingCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShoppingCategoryOption>>(_categories.Values.ToList());
}
