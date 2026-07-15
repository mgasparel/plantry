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
        var expansion = new RecipeExpansionService(recipes);
        var service = new DietTagNudgeService(
            recipes, expansion, tags, products, new FakeAiAssistanceGateReader(gateEnabled), checker, clock,
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
        // The user already reconciled this exact set. With no inclusions, the expanded set == the direct set,
        // so the aggregate's direct hash is the expanded-set hash the guard will compare against.
        h.Recipe.DismissDietNudge(h.Recipe.CurrentIngredientProductHash(), SystemClock.Instance);

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
        h.Recipe.DismissDietNudge(h.Recipe.CurrentIngredientProductHash(), SystemClock.Instance);
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

    // ── Expanded product set over inclusions (recipe-composition.md §8 / D9) ────

    private static readonly Guid SubDairyId = Guid.Parse("11111111-0003-0000-0000-000000000003");

    private sealed record InclusionHarness(
        DietTagNudgeService Service,
        RecordingDietChecker Checker,
        FakeRecipeRepository Recipes,
        Recipe Parent,
        Recipe Sub,
        TagId DietTagId);

    /// <summary>
    /// A Vegan-tagged PARENT whose only DIRECT ingredient is vegan-safe pasta but which INCLUDES a sub-recipe
    /// carrying a dairy product (Parmesan). The parent's direct product set never mentions the dairy — only the
    /// EXPANDED set does — so this is exactly the D9 case: a dairy product inside a sub of a Vegan-tagged parent
    /// must be caught. The expansion service is wired over the same in-memory repo.
    /// </summary>
    private static InclusionHarness BuildWithInclusion(
        bool gateEnabled = true,
        IReadOnlyList<DietTagContradiction>? canned = null,
        ICatalogProductReader? productsOverride = null)
    {
        var clock = SystemClock.Instance;
        var household = DomainHousehold.From(HouseholdAId);

        ICatalogProductReader products;
        if (productsOverride is not null)
        {
            products = productsOverride;
        }
        else
        {
            var fake = new FakeCatalogProductReader();
            fake.RegisterTracked(PastaId, "Rigatoni");
            fake.RegisterTracked(SubDairyId, "Parmesan");
            products = fake;
        }

        var tags = new FakeTagRepository();
        var vegan = Tag.Create(household, "Vegan", TagCategory.Diet, clock);
        tags.Items.Add(vegan);

        var recipes = new FakeRecipeRepository();

        var sub = Recipe.Create(household, "Cheese Sauce", 2, clock).Value;
        sub.ReplaceLines(RecipeLineSet.Create([new IngredientLine(SubDairyId, 100m, UnitId, null, 0)], [], sub.Id).Value, clock);
        recipes.Items.Add(sub);

        var parent = Recipe.Create(household, "Vegan Nachos", 2, clock).Value;
        parent.ReplaceLines(
            RecipeLineSet.Create(
                [new IngredientLine(PastaId, 200m, UnitId, null, 0)],
                [new InclusionLine(sub.Id, 2m, null, 1)],
                parent.Id).Value,
            clock);
        parent.SetTags([vegan.Id], clock);
        recipes.Items.Add(parent);

        var checker = new RecordingDietChecker(
            canned ?? [new DietTagContradiction("Parmesan", "Vegan")]);
        var expansion = new RecipeExpansionService(recipes);
        var service = new DietTagNudgeService(
            recipes, expansion, tags, products, new FakeAiAssistanceGateReader(gateEnabled), checker, clock,
            NullLogger<DietTagNudgeService>.Instance);

        return new InclusionHarness(service, checker, recipes, parent, sub, vegan.Id);
    }

    [Fact]
    public async Task ShouldOffer_True_When_An_Inclusion_Adds_A_Product_To_The_Expanded_Set()
    {
        var h = BuildWithInclusion();
        // Before this save the recipe had only its direct pasta; including the sub added its dairy to the
        // EXPANDED set — a change caused only by editing which recipes are included (acceptance criterion 1).
        var offer = await h.Service.ShouldOfferAfterSaveAsync(h.Parent.Id, new HashSet<Guid> { PastaId });
        Assert.True(offer);
    }

    [Fact]
    public async Task ShouldOffer_False_When_The_Expanded_Set_Is_Unchanged()
    {
        var h = BuildWithInclusion();
        // The pre-save expanded set already carried the sub's dairy — an effective-neutral edit (criterion 1).
        var offer = await h.Service.ShouldOfferAfterSaveAsync(
            h.Parent.Id, new HashSet<Guid> { PastaId, SubDairyId });
        Assert.False(offer);
    }

    [Fact]
    public async Task ShouldOffer_Cheap_Path_Never_Resolves_Catalog_Names_Or_Calls_The_Checker()
    {
        // A reader that throws on every call proves the cheap guard decides from ids + hashes alone — no LLM and
        // no Catalog name resolution (acceptance criterion 4).
        var h = BuildWithInclusion(productsOverride: new ThrowingCatalogProductReader());
        var offer = await h.Service.ShouldOfferAfterSaveAsync(h.Parent.Id, new HashSet<Guid> { PastaId });
        Assert.True(offer);
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task Evaluate_Sends_The_Expanded_Sub_Recipe_Names_To_The_Checker()
    {
        var h = BuildWithInclusion();
        var view = await h.Service.EvaluateAsync(h.Parent.Id);

        Assert.NotNull(view);
        Assert.Equal("Parmesan", view!.IngredientName);
        Assert.Equal("Vegan", view.DietTagName);
        Assert.Equal(h.DietTagId.Value, view.DietTagId);
        // The dairy product lives in the SUB, not among the parent's direct ingredients, yet its name reached
        // the checker via the expanded set (D9). The direct pasta is present too.
        Assert.Contains("Parmesan", h.Checker.LastIngredientNames!);
        Assert.Contains("Rigatoni", h.Checker.LastIngredientNames!);
    }

    [Fact]
    public async Task Dismiss_Stores_The_Expanded_Hash_And_Changing_A_Sub_Reoffers()
    {
        var clock = SystemClock.Instance;
        var h = BuildWithInclusion();

        // Dismiss records the EXPANDED-set hash ({pasta, parmesan}), NOT the parent's direct hash ({pasta}).
        await h.Service.DismissAsync(h.Parent.Id);
        var expandedHash = Recipe.IngredientProductHash([PastaId, SubDairyId]);
        Assert.Equal(expandedHash, h.Parent.DietNudgeDismissedHash);
        Assert.NotEqual(h.Parent.CurrentIngredientProductHash(), h.Parent.DietNudgeDismissedHash);

        // A later no-effective-change save (same expanded set) does not re-nag (acceptance criterion 2).
        Assert.False(await h.Service.ShouldOfferAfterSaveAsync(
            h.Parent.Id, new HashSet<Guid> { PastaId, SubDairyId }));

        // The user swaps the included sub for one carrying a DIFFERENT product — the expanded set changes.
        var otherProduct = Guid.Parse("11111111-0003-0000-0000-000000000009");
        var newSub = Recipe.Create(DomainHousehold.From(HouseholdAId), "Nut Cream", 2, clock).Value;
        newSub.ReplaceLines(RecipeLineSet.Create([new IngredientLine(otherProduct, 100m, UnitId, null, 0)], [], newSub.Id).Value, clock);
        h.Recipes.Items.Add(newSub);
        h.Parent.ReplaceLines(
            RecipeLineSet.Create(
                [new IngredientLine(PastaId, 200m, UnitId, null, 0)],
                [new InclusionLine(newSub.Id, 2m, null, 1)],
                h.Parent.Id).Value,
            clock);

        // The editor passes the PRE-edit expanded set; the new expanded set differs from it AND from the
        // dismissed hash, so changing a sub's inclusion re-offers (acceptance criterion 2).
        Assert.True(await h.Service.ShouldOfferAfterSaveAsync(
            h.Parent.Id, new HashSet<Guid> { PastaId, SubDairyId }));
    }

    // ── Reverse ripple: sub save nudges diet-tagged includers (recipe-composition.md §8 / D10) ──

    [Fact]
    public async Task Ripple_Flags_A_Diet_Tagged_Includer_Whose_Expanded_Set_Is_Unreconciled()
    {
        var h = BuildWithInclusion();
        // Saving the SUB reverse-looks-up its includers: the Vegan parent's expanded set carries the sub's dairy and
        // has never been reconciled, so the cheap guard flags it — with no LLM and no Catalog name resolution
        // (acceptance criterion 1). Editing the sub, not the parent, is exactly the case with no parent save to fire.
        var parents = await h.Service.IncludersNeedingRippleNudgeAsync(h.Sub.Id);
        Assert.Equal([h.Parent.Id], parents);
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task Ripple_Skips_An_Includer_Already_Reconciled_For_Its_Expanded_Set()
    {
        var h = BuildWithInclusion();
        // The parent already dismissed its current expanded set ({pasta, parmesan}); a sub save that leaves that set
        // unchanged must not re-nag it (per-parent dismissal, acceptance criterion 1 — "already reconciled skipped").
        h.Parent.DismissDietNudge(Recipe.IngredientProductHash([PastaId, SubDairyId]), SystemClock.Instance);
        var parents = await h.Service.IncludersNeedingRippleNudgeAsync(h.Sub.Id);
        Assert.Empty(parents);
    }

    [Fact]
    public async Task Ripple_Skips_A_Non_Diet_Tagged_Includer()
    {
        var h = BuildWithInclusion();
        // Strip the parent's Vegan tag: it still includes the sub, but with no Diet-category tag there is nothing to
        // contradict, so it is skipped before any expansion or LLM (acceptance criterion 4).
        h.Parent.SetTags([], SystemClock.Instance);
        var parents = await h.Service.IncludersNeedingRippleNudgeAsync(h.Sub.Id);
        Assert.Empty(parents);
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task Ripple_With_No_Includers_Returns_Empty_And_Never_Calls_The_Checker()
    {
        // A flat recipe that nothing includes: the reverse lookup is empty, so the guard returns immediately with no
        // expansion and no LLM — zero work beyond the includers lookup (acceptance criterion 4).
        var h = Build();
        var parents = await h.Service.IncludersNeedingRippleNudgeAsync(h.Recipe.Id);
        Assert.Empty(parents);
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task Ripple_Reaches_A_Transitive_Diet_Tagged_Includer()
    {
        var clock = SystemClock.Instance;
        var household = DomainHousehold.From(HouseholdAId);

        var products = new FakeCatalogProductReader();
        products.RegisterTracked(PastaId, "Rigatoni");
        products.RegisterTracked(SubDairyId, "Parmesan");

        var tags = new FakeTagRepository();
        var vegan = Tag.Create(household, "Vegan", TagCategory.Diet, clock);
        tags.Items.Add(vegan);

        var recipes = new FakeRecipeRepository();

        // sub (dairy) ← parent ← grandparent(Vegan). Editing the sub must ripple to the transitive grandparent.
        var sub = Recipe.Create(household, "Cheese Sauce", 2, clock).Value;
        sub.ReplaceLines(RecipeLineSet.Create([new IngredientLine(SubDairyId, 100m, UnitId, null, 0)], [], sub.Id).Value, clock);
        recipes.Items.Add(sub);

        var parent = Recipe.Create(household, "Nacho Layer", 2, clock).Value;
        parent.ReplaceLines(RecipeLineSet.Create([], [new InclusionLine(sub.Id, 1m, null, 0)], parent.Id).Value, clock);
        recipes.Items.Add(parent);

        var grandparent = Recipe.Create(household, "Vegan Nachos", 2, clock).Value;
        grandparent.ReplaceLines(
            RecipeLineSet.Create(
                [new IngredientLine(PastaId, 200m, UnitId, null, 0)],
                [new InclusionLine(parent.Id, 1m, null, 1)],
                grandparent.Id).Value,
            clock);
        grandparent.SetTags([vegan.Id], clock);
        recipes.Items.Add(grandparent);

        var checker = new RecordingDietChecker([new DietTagContradiction("Parmesan", "Vegan")]);
        var service = new DietTagNudgeService(
            recipes, new RecipeExpansionService(recipes), tags, products,
            new FakeAiAssistanceGateReader(true), checker, clock, NullLogger<DietTagNudgeService>.Instance);

        // The untagged intermediate parent is skipped; only the diet-tagged grandparent is flagged.
        var flagged = await service.IncludersNeedingRippleNudgeAsync(sub.Id);
        Assert.Equal([grandparent.Id], flagged);
    }

    [Fact]
    public async Task EvaluateRipple_Names_The_Parent_And_Maps_The_Contradiction_To_Its_Diet_Tag()
    {
        var h = BuildWithInclusion();
        var view = await h.Service.EvaluateRippleAsync(h.Parent.Id);

        Assert.NotNull(view);
        Assert.Equal(h.Parent.Id.Value, view!.ParentRecipeId);
        Assert.Equal("Vegan Nachos", view.ParentName); // the landing is the sub's page, so the parent is named (D10)
        Assert.Equal("Parmesan", view.IngredientName);
        Assert.Equal("Vegan", view.DietTagName);
        Assert.Equal(h.DietTagId.Value, view.DietTagId); // "Remove tag" / dismissal target the PARENT's own tag
    }

    [Fact]
    public async Task EvaluateRipple_Gate_Off_Returns_Null_And_Never_Calls_The_Checker()
    {
        var h = BuildWithInclusion(gateEnabled: false);
        Assert.Null(await h.Service.EvaluateRippleAsync(h.Parent.Id)); // AI gate off ⇒ no LLM, no nudge (criterion 3)
        Assert.False(h.Checker.WasCalled);
    }

    [Fact]
    public async Task EvaluateRipple_Returns_Null_When_The_Parent_Set_Is_Already_Reconciled()
    {
        var h = BuildWithInclusion();
        h.Parent.DismissDietNudge(Recipe.IngredientProductHash([PastaId, SubDairyId]), SystemClock.Instance);
        Assert.Null(await h.Service.EvaluateRippleAsync(h.Parent.Id));
        Assert.False(h.Checker.WasCalled); // short-circuits before the LLM, like the direct nudge
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

/// <summary>
/// An <see cref="ICatalogProductReader"/> that throws on every call — used to prove the cheap post-save guard
/// (<see cref="DietTagNudgeService.ShouldOfferAfterSaveAsync"/>) resolves no Catalog names at all (criterion 4).
/// </summary>
internal sealed class ThrowingCatalogProductReader : ICatalogProductReader
{
    private static InvalidOperationException Fail() =>
        new("The cheap diet-nudge guard must not touch the Catalog reader.");

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) => throw Fail();

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default) =>
        throw Fail();

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default) => throw Fail();

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) => throw Fail();

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) => throw Fail();

    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) => throw Fail();

    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        throw Fail();
}
