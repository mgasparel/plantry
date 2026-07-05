using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Application;

/// <summary>
/// L1 tests for <see cref="SuggestRecipeTags"/> — the application service behind the recipe editor's
/// edit-moment tag suggestion (plantry-qll2.2). Verifies the acceptance-criterion behaviours at the
/// orchestration layer with the LLM faked out:
/// <list type="bullet">
///   <item>Gate OFF ⇒ no suggester call, empty result (criterion 3 — toggle off, no call, no chips).</item>
///   <item>Ingredient names are resolved from product ids via the Catalog ACL (Ingredient has no name)
///   and the active tag vocabulary is passed to the suggester (criterion 1 — chips from household vocab).</item>
///   <item>Empty/unresolvable input short-circuits before any LLM call.</item>
/// </list>
/// </summary>
public sealed class SuggestRecipeTagsTests
{
    private static readonly Guid HouseholdAId = Guid.Parse("cccccccc-0002-0000-0000-000000000001");
    private static readonly Guid ChickenProductId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid CreamProductId = Guid.Parse("11111111-0000-0000-0000-000000000002");

    private static (SuggestRecipeTags Service, RecordingSuggester Suggester, FakeTagRepository Tags)
        BuildService(bool gateEnabled)
    {
        var gate = new FakeAiAssistanceGateReader(gateEnabled);

        var products = new FakeCatalogProductReader();
        products.RegisterTracked(ChickenProductId, "Chicken Breast");
        products.RegisterTracked(CreamProductId, "Heavy Cream");

        var tags = new FakeTagRepository();
        var clock = SystemClock.Instance;
        var household = HouseholdId.From(HouseholdAId);
        tags.Items.Add(Tag.Create(household, "Chicken", TagCategory.Protein, clock));
        tags.Items.Add(Tag.Create(household, "Quick", null, clock));

        var suggester = new RecordingSuggester(
        [
            new TagSuggestion("Chicken", TagCategory.Protein, Guid.NewGuid()),
        ]);

        return (new SuggestRecipeTags(gate, products, tags, suggester), suggester, tags);
    }

    [Fact]
    public async Task Gate_Off_Returns_Empty_And_Never_Calls_The_Suggester()
    {
        var (service, suggester, _) = BuildService(gateEnabled: false);

        var result = await service.ExecuteAsync([ChickenProductId, CreamProductId]);

        Assert.Empty(result);
        Assert.False(suggester.WasCalled);
    }

    [Fact]
    public async Task Empty_Product_List_Returns_Empty_Without_Calling_The_Suggester()
    {
        var (service, suggester, _) = BuildService(gateEnabled: true);

        var result = await service.ExecuteAsync([]);

        Assert.Empty(result);
        Assert.False(suggester.WasCalled);
    }

    [Fact]
    public async Task Unresolvable_Products_Return_Empty_Without_Calling_The_Suggester()
    {
        var (service, suggester, _) = BuildService(gateEnabled: true);

        // A product id not registered in the catalog resolves to no name.
        var result = await service.ExecuteAsync([Guid.NewGuid()]);

        Assert.Empty(result);
        Assert.False(suggester.WasCalled);
    }

    [Fact]
    public async Task Gate_On_Resolves_Ingredient_Names_And_Vocabulary_And_Returns_Suggestions()
    {
        var (service, suggester, _) = BuildService(gateEnabled: true);

        var result = await service.ExecuteAsync([ChickenProductId, CreamProductId]);

        Assert.True(suggester.WasCalled);
        // Ingredient names came from the Catalog ACL (Ingredient carries no Name field).
        Assert.Contains("Chicken Breast", suggester.LastIngredientNames!);
        Assert.Contains("Heavy Cream", suggester.LastIngredientNames!);
        // Active household vocabulary (names + categories) was passed through.
        Assert.Contains(suggester.LastVocabulary!, v => v.Name == "Chicken" && v.Category == TagCategory.Protein);
        Assert.Contains(suggester.LastVocabulary!, v => v.Name == "Quick");
        // The suggester's proposals flow back unchanged.
        Assert.Single(result);
    }

    [Fact]
    public async Task Collapses_Duplicate_Product_Ids_Before_Resolving()
    {
        var (service, suggester, _) = BuildService(gateEnabled: true);

        await service.ExecuteAsync([ChickenProductId, ChickenProductId, ChickenProductId]);

        Assert.True(suggester.WasCalled);
        Assert.Single(suggester.LastIngredientNames!);
    }
}

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakeAiAssistanceGateReader(bool enabled) : IAiAssistanceGateReader
{
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(enabled);
}

/// <summary>Records the arguments the service passed and returns a canned proposal set.</summary>
internal sealed class RecordingSuggester(IReadOnlyList<TagSuggestion> canned) : IRecipeTagSuggester
{
    public bool WasCalled { get; private set; }
    public IReadOnlyList<string>? LastIngredientNames { get; private set; }
    public IReadOnlyList<TagVocabularyEntry>? LastVocabulary { get; private set; }

    public Task<IReadOnlyList<TagSuggestion>> SuggestAsync(
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<TagVocabularyEntry> vocabulary,
        CancellationToken ct = default)
    {
        WasCalled = true;
        LastIngredientNames = ingredientNames;
        LastVocabulary = vocabulary;
        return Task.FromResult(canned);
    }
}
