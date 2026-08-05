using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Planning.Application;
using Plantry.Tests.Web.Infrastructure;
using System.Text.Json;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// Regression coverage for plantry-8cl1: the dish-search JSON's recipe hits built <c>photoUrl</c> as
/// <c>/Recipes/Details?id={id}&amp;handler=Photo</c> — the recipe *page* route with a handler query
/// param — instead of the actual photo endpoint <c>/Recipes/{id}?handler=Photo</c> (Details.cshtml
/// declares <c>@page "/Recipes/{id:guid}"</c>; there is no <c>/Recipes/Details</c> route, so the old
/// URL never matched and the browser rendered a broken image). Asserts the fixed shape for a
/// <c>hasPhoto = true</c> recipe hit from <c>GET ?handler=SearchJson</c>.
/// </summary>
public sealed class MealPlanSearchPhotoUrlTests
{
    [Fact(DisplayName = "GET ?handler=SearchJson recipe hit with hasPhoto=true carries the real photo endpoint, not the /Recipes/Details page route")]
    public async Task SearchJson_RecipeHitWithPhoto_UsesRealPhotoEndpoint()
    {
        await using var factory = new RecipePhotoSearchFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());

        var response = await client.GetAsync("/MealPlan?handler=SearchJson&q=Pancakes");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var hits = doc.RootElement.GetProperty("hits");
        Assert.Equal(1, hits.GetArrayLength());
        var hit = hits[0];
        Assert.Equal("recipe", hit.GetProperty("kind").GetString());
        Assert.True(hit.GetProperty("hasPhoto").GetBoolean());

        var photoUrl = hit.GetProperty("photoUrl").GetString();
        Assert.Equal($"/Recipes/{RecipePhotoSearchFixture.RecipeId:D}?handler=Photo", photoUrl);
        Assert.DoesNotContain("/Recipes/Details", photoUrl);
    }
}

// ── Fixture ───────────────────────────────────────────────────────────────────

internal static class RecipePhotoSearchFixture
{
    public static readonly Guid RecipeId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");

    public static readonly IReadOnlyList<RecipeReadModel> Recipes =
        [new RecipeReadModel(RecipeId, "Pancakes", [], 4, HasPhoto: true)];
}

// ── Factory ───────────────────────────────────────────────────────────────────

/// <summary>
/// WAF factory for plantry-8cl1: overrides only the recipe reader, with a single hasPhoto=true recipe.
/// Everything else takes <see cref="MealPlanFragmentFactory"/>'s defaults — OnGetSearchJsonAsync never
/// touches the meal-plan/slot-config/member/catalog fakes for this scenario.
/// </summary>
public sealed class RecipePhotoSearchFactory : MealPlanFragmentFactory
{
    protected override IRecipeReadModel RecipeReadModel =>
        new FakeRecipeReader(RecipePhotoSearchFixture.Recipes);
}
