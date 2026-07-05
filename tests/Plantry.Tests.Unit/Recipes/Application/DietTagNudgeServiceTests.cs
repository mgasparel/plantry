using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel.Domain;
using DomainHousehold = Plantry.SharedKernel.HouseholdId;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L1 tests for <see cref="DietTagNudgeService"/> — the orchestration behind the edit-moment diet-tag
/// contradiction nudge (plantry-qll2.3), with the LLM checker faked. Proves the acceptance-criterion behaviours
/// at the seam the editor POST and the deferred Details check call:
/// <list type="bullet">
///   <item><b>ShouldOfferAfterSaveAsync</b> — the cheap post-save guard: fires only on an actual ingredient-set
///   change (criterion 2), only with a Diet-category tag (criterion 3), and not for an already-dismissed set
///   (criterion 1). No LLM.</item>
///   <item><b>EvaluateAsync</b> — gate OFF ⇒ no checker call, null (criterion 4); a real clash ⇒ a nudge mapped to
///   the recipe's own diet-tag id; no clash ⇒ null.</item>
///   <item><b>DismissAsync / RemoveTagAsync</b> — record the reconciled hash (and, for remove, drop the tag the
///   user chose) so the nudge does not re-nag.</item>
/// </list>
/// </summary>
public sealed class DietTagNudgeServiceTests
{
    private static readonly Guid HouseholdAId = Guid.Parse("dddddddd-0003-0000-0000-000000000001");
    private static readonly Guid PastaId = Guid.Parse("11111111-0003-0000-0000-000000000001");
    private static readonly Guid ParmesanId = Guid.Parse("11111111-0003-0000-0000-000000000002");
    private static readonly Guid UnitId = Guid.Parse("22222222-0003-0000-0000-000000000001");

    private sealed record Harness(
        DietTagNudgeService Service,
        RecordingDietChecker Checker,
        FakeRecipeRepository Recipes,
        Recipe Recipe,
        TagId DietTagId);

    private static Harness Build(
        bool gateEnabled = true,
        IReadOnlyList<DietTagContradiction>? canned = null,
        bool applyDietTag = true)
    {
        var clock = SystemClock.Instance;
        var household = DomainHousehold.From(HouseholdAId);

        var products = new FakeCatalogProductReader();
        products.RegisterTracked(PastaId, "Rigatoni");
        products.RegisterTracked(ParmesanId, "Parmesan");

        var tags = new FakeTagRepository();
        var dairyFree = Tag.Create(household, "Dairy-Free", TagCategory.Diet, clock);
        tags.Items.Add(dairyFree);

        var recipes = new FakeRecipeRepository();
        var recipe = Recipe.Create(household, "Alfredo", 2, clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId, 200m, UnitId, null, 0),
            new IngredientLine(ParmesanId, 50m, UnitId, null, 1),
        ], clock);
        if (applyDietTag)
            recipe.SetTags([dairyFree.Id], clock);
        recipes.Items.Add(recipe);

        var checker = new RecordingDietChecker(
            canned ?? [new DietTagContradiction("Parmesan", "Dairy-Free")]);
        var service = new DietTagNudgeService(
            recipes, tags, products, new FakeAiAssistanceGateReader(gateEnabled), checker, clock,
            NullLogger<DietTagNudgeService>.Instance);

        return new Harness(service, checker, recipes, recipe, dairyFree.Id);
    }

    // ── ShouldOfferAfterSaveAsync (the cheap post-save guard) ──────────────────

    [Fact]
    public async Task ShouldOffer_False_When_Ingredient_Set_Unchanged()
    {
        var h = Build();
        // previous set == current set ⇒ ingredient-neutral edit (criterion 2).
        var offer = await h.Service.ShouldOfferAfterSaveAsync(
            h.Recipe.Id, new HashSet<Guid> { PastaId, ParmesanId });
        Assert.False(offer);
    }

    [Fact]
    public async Task ShouldOffer_True_When_A_ProductId_Was_Added_To_A_Diet_Tagged_Recipe()
    {
        var h = Build();
        // Parmesan is new relative to the previous {Pasta} set.
        var offer = await h.Service.ShouldOfferAfterSaveAsync(
            h.Recipe.Id, new HashSet<Guid> { PastaId });
        Assert.True(offer);
    }

    [Fact]
    public async Task ShouldOffer_False_When_No_Diet_Tag()
    {
        var h = Build(applyDietTag: false);
        var offer = await h.Service.ShouldOfferAfterSaveAsync(
            h.Recipe.Id, new HashSet<Guid> { PastaId });
        Assert.False(offer); // criterion 3
    }

    [Fact]
    public async Task ShouldOffer_False_When_The_Current_Set_Was_Already_Dismissed()
    {
        var h = Build();
        // The user already reconciled this exact ingredient set.
        h.Recipe.DismissDietNudge(SystemClock.Instance);

        var offer = await h.Service.ShouldOfferAfterSaveAsync(
            h.Recipe.Id, new HashSet<Guid> { PastaId }); // set genuinely changed, but already dismissed
        Assert.False(offer); // criterion 1 — dismissal remembered
    }

    // ── EvaluateAsync (the deferred gate + LLM check) ──────────────────────────

    [Fact]
    public async Task Evaluate_Gate_Off_Returns_Null_And_Never_Calls_The_Checker()
    {
        var h = Build(gateEnabled: false);
        var view = await h.Service.EvaluateAsync(h.Recipe.Id);
        Assert.Null(view);            // criterion 4
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task Evaluate_Returns_The_First_Contradiction_Mapped_To_The_Recipe_Diet_Tag_Id()
    {
        var h = Build();
        var view = await h.Service.EvaluateAsync(h.Recipe.Id);

        Assert.NotNull(view);
        Assert.Equal("Parmesan", view!.IngredientName);
        Assert.Equal("Dairy-Free", view.DietTagName);
        Assert.Equal(h.DietTagId.Value, view.DietTagId); // resolves the id for the "Remove tag" action
        // Ingredient names were resolved via the Catalog ACL (Ingredient carries no Name).
        Assert.Contains("Parmesan", h.Checker.LastIngredientNames!);
        Assert.Contains("Dairy-Free", h.Checker.LastDietTagNames!);
    }

    [Fact]
    public async Task Evaluate_Returns_Null_When_The_Checker_Finds_No_Contradiction()
    {
        var h = Build(canned: []);
        Assert.Null(await h.Service.EvaluateAsync(h.Recipe.Id));
        Assert.True(h.Checker.WasCalled); // it ran, and honestly found nothing
    }

    [Fact]
    public async Task Evaluate_Returns_Null_When_The_Set_Is_Already_Dismissed()
    {
        var h = Build();
        h.Recipe.DismissDietNudge(SystemClock.Instance);
        Assert.Null(await h.Service.EvaluateAsync(h.Recipe.Id));
        Assert.False(h.Checker.WasCalled); // short-circuits before the LLM
    }

    // ── DismissAsync / RemoveTagAsync (reconciliation writes) ──────────────────

    [Fact]
    public async Task Dismiss_Records_The_Current_Set_Hash_And_Saves()
    {
        var h = Build();
        await h.Service.DismissAsync(h.Recipe.Id);

        Assert.Equal(h.Recipe.CurrentIngredientProductHash(), h.Recipe.DietNudgeDismissedHash);
        Assert.Equal(1, h.Recipes.SaveChangesCalls);
        // The tag is untouched — "Keep it" keeps the tag.
        Assert.Contains(h.DietTagId, h.Recipe.Tags.Select(rt => rt.TagId));
    }

    [Fact]
    public async Task RemoveTag_Drops_The_Tag_The_User_Chose_And_Records_The_Hash()
    {
        var h = Build();
        await h.Service.RemoveTagAsync(h.Recipe.Id, h.DietTagId.Value);

        Assert.DoesNotContain(h.DietTagId, h.Recipe.Tags.Select(rt => rt.TagId));
        Assert.Equal(h.Recipe.CurrentIngredientProductHash(), h.Recipe.DietNudgeDismissedHash);
        Assert.Equal(1, h.Recipes.SaveChangesCalls);
    }
}

/// <summary>Records the arguments the service passed and returns a canned contradiction set.</summary>
internal sealed class RecordingDietChecker(IReadOnlyList<DietTagContradiction> canned) : IDietTagContradictionChecker
{
    public bool WasCalled { get; private set; }
    public IReadOnlyList<string>? LastIngredientNames { get; private set; }
    public IReadOnlyList<string>? LastDietTagNames { get; private set; }

    public Task<IReadOnlyList<DietTagContradiction>> CheckAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> dietTagNames,
        CancellationToken ct = default)
    {
        WasCalled = true;
        LastIngredientNames = ingredientNames;
        LastDietTagNames = dietTagNames;
        return Task.FromResult(canned);
    }
}
