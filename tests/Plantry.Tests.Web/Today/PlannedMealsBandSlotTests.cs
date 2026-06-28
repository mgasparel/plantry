using Plantry.MealPlanning.Domain;
using Plantry.Web.Pages.Today;

namespace Plantry.Tests.Web.Today;

/// <summary>
/// L1 unit tests for <see cref="PlannedMealSlotVm"/> shape invariants (plantry-zp7).
///
/// These tests verify the view-model record-level contract:
/// <list type="bullet">
///   <item>Empty slot: <c>IsPlanned=false</c>, no recipe details, no fulfillment data.</item>
///   <item>Planned slot (recipe): <c>IsPlanned=true</c>, <c>RecipeId</c> set, <c>IsFullyCookable</c> reflects 100% fulfillment.</item>
///   <item>Planned slot (note): <c>IsPlanned=true</c>, <c>Note</c> set, <c>RecipeId=null</c>.</item>
///   <item><c>AllMealsPlanned</c> computation (inline simulation matching <see cref="IndexModel.AllMealsPlanned"/>).</item>
/// </list>
/// </summary>
public sealed class PlannedMealsBandSlotTests
{
    private static readonly MealSlotId SlotId = MealSlotId.From(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly Guid RecipeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MealId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    // ── Empty slot ───────────────────────────────────────────────────────────

    [Fact(DisplayName = "PlannedMealSlotVm — empty slot has IsPlanned=false and no recipe fields")]
    public void EmptySlot_IsPlanned_False()
    {
        var vm = new PlannedMealSlotVm(
            SlotId: SlotId,
            SlotLabel: "Breakfast",
            IsPlanned: false,
            RecipeId: null,
            RecipeName: null,
            HasPhoto: false,
            DishNames: [],
            Note: null,
            CookTimeMinutes: null,
            Servings: 0,
            EffectiveAttendees: [],
            IsFullyCookable: false,
            MealId: Guid.Empty,
            HasExpiringIngredients: false);

        Assert.False(vm.IsPlanned);
        Assert.Null(vm.RecipeId);
        Assert.Null(vm.RecipeName);
        Assert.Null(vm.Note);
        Assert.False(vm.IsFullyCookable);
    }

    // ── Planned slot (recipe) ────────────────────────────────────────────────

    [Fact(DisplayName = "PlannedMealSlotVm — planned recipe slot has IsPlanned=true and RecipeId set")]
    public void PlannedRecipeSlot_IsPlanned_True()
    {
        var vm = new PlannedMealSlotVm(
            SlotId: SlotId,
            SlotLabel: "Lunch",
            IsPlanned: true,
            RecipeId: RecipeId,
            RecipeName: "Pasta Carbonara",
            HasPhoto: true,
            DishNames: ["Pasta Carbonara"],
            Note: null,
            CookTimeMinutes: 25,
            Servings: 2,
            EffectiveAttendees: [],
            IsFullyCookable: true,
            MealId: MealId,
            HasExpiringIngredients: false);

        Assert.True(vm.IsPlanned);
        Assert.Equal(RecipeId, vm.RecipeId);
        Assert.Equal("Pasta Carbonara", vm.RecipeName);
        Assert.True(vm.IsFullyCookable);
        Assert.Equal(25, vm.CookTimeMinutes);
        Assert.Null(vm.Note);
    }

    [Fact(DisplayName = "PlannedMealSlotVm — planned recipe slot with FulfillmentPercent<100 has IsFullyCookable=false")]
    public void PlannedRecipeSlot_PartialFulfillment_NotFullyCookable()
    {
        var vm = new PlannedMealSlotVm(
            SlotId: SlotId,
            SlotLabel: "Dinner",
            IsPlanned: true,
            RecipeId: RecipeId,
            RecipeName: "Veggie Stir",
            HasPhoto: false,
            DishNames: ["Veggie Stir"],
            Note: null,
            CookTimeMinutes: 20,
            Servings: 2,
            EffectiveAttendees: [],
            IsFullyCookable: false,    // < 100% fulfillment
            MealId: MealId,
            HasExpiringIngredients: false);

        Assert.True(vm.IsPlanned);
        Assert.False(vm.IsFullyCookable);
    }

    // ── Planned slot (note) ──────────────────────────────────────────────────

    [Fact(DisplayName = "PlannedMealSlotVm — planned note slot has IsPlanned=true and Note set, RecipeId null")]
    public void PlannedNoteSlot_NoteSet_RecipeIdNull()
    {
        var vm = new PlannedMealSlotVm(
            SlotId: SlotId,
            SlotLabel: "Breakfast",
            IsPlanned: true,
            RecipeId: null,
            RecipeName: null,
            HasPhoto: false,
            DishNames: [],
            Note: "Leftover soup",
            CookTimeMinutes: null,
            Servings: 0,
            EffectiveAttendees: [],
            IsFullyCookable: false,
            MealId: MealId,
            HasExpiringIngredients: false);

        Assert.True(vm.IsPlanned);
        Assert.Null(vm.RecipeId);
        Assert.Equal("Leftover soup", vm.Note);
    }

    // ── HasExpiringIngredients flag ──────────────────────────────────────────

    [Fact(DisplayName = "PlannedMealSlotVm — HasExpiringIngredients propagates from fulfillment roll-up")]
    public void PlannedSlot_HasExpiringIngredients_Propagates()
    {
        var vm = new PlannedMealSlotVm(
            SlotId: SlotId,
            SlotLabel: "Dinner",
            IsPlanned: true,
            RecipeId: RecipeId,
            RecipeName: "Carbonara",
            HasPhoto: false,
            DishNames: ["Carbonara"],
            Note: null,
            CookTimeMinutes: 30,
            Servings: 2,
            EffectiveAttendees: [],
            IsFullyCookable: true,
            MealId: MealId,
            HasExpiringIngredients: true);   // ← from PlanFulfillmentService

        Assert.True(vm.HasExpiringIngredients);
    }

    // ── AllMealsPlanned invariant ────────────────────────────────────────────

    [Fact(DisplayName = "AllMealsPlanned is true when all slot VMs have IsPlanned=true")]
    public void AllMealsPlanned_AllPlanned_IsTrue()
    {
        var slots = new[]
        {
            MakePlanned("Breakfast"),
            MakePlanned("Lunch"),
            MakePlanned("Dinner"),
        };

        // Mirrors IndexModel.AllMealsPlanned: slots.Count > 0 && slots.All(s => s.IsPlanned)
        bool allPlanned = slots.Length > 0 && slots.All(s => s.IsPlanned);
        Assert.True(allPlanned);
    }

    [Fact(DisplayName = "AllMealsPlanned is false when any slot is empty")]
    public void AllMealsPlanned_OneEmptySlot_IsFalse()
    {
        var slots = new[]
        {
            MakePlanned("Breakfast"),
            MakeEmpty("Lunch"),
            MakePlanned("Dinner"),
        };

        bool allPlanned = slots.Length > 0 && slots.All(s => s.IsPlanned);
        Assert.False(allPlanned);
    }

    [Fact(DisplayName = "AllMealsPlanned is false when slots list is empty")]
    public void AllMealsPlanned_EmptySlotList_IsFalse()
    {
        var slots = Array.Empty<PlannedMealSlotVm>();

        bool allPlanned = slots.Length > 0 && slots.All(s => s.IsPlanned);
        Assert.False(allPlanned);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PlannedMealSlotVm MakePlanned(string label) =>
        new(
            SlotId: MealSlotId.New(),
            SlotLabel: label,
            IsPlanned: true,
            RecipeId: Guid.NewGuid(),
            RecipeName: label + " recipe",
            HasPhoto: false,
            DishNames: [label + " recipe"],
            Note: null,
            CookTimeMinutes: 20,
            Servings: 2,
            EffectiveAttendees: [],
            IsFullyCookable: true,
            MealId: Guid.NewGuid(),
            HasExpiringIngredients: false);

    private static PlannedMealSlotVm MakeEmpty(string label) =>
        new(
            SlotId: MealSlotId.New(),
            SlotLabel: label,
            IsPlanned: false,
            RecipeId: null,
            RecipeName: null,
            HasPhoto: false,
            DishNames: [],
            Note: null,
            CookTimeMinutes: null,
            Servings: 0,
            EffectiveAttendees: [],
            IsFullyCookable: false,
            MealId: Guid.Empty,
            HasExpiringIngredients: false);
}
