using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render test for the Recipe Detail page's home-produced exclusion (plantry-4osq): a recipe
/// ingredient whose product is <c>Product.IsProduced</c> (a recipe yield, cook leftover, or garden
/// produce — the case that surfaced the bug) must never be counted by either the "Add all ingredients"
/// or "Add missing" button, even though it is stock-tracked and would otherwise read Missing.
///
/// <para>Directly exercises the acceptance criterion the unit-level
/// <c>AddIngredientsToShoppingListTests</c>/<c>AddMissingToShoppingListTests</c> coverage cannot reach:
/// "Button labels/counts on Recipe Details match what each button actually syncs (no plantry-gsj
/// drift)". The Details page model computes both buttons' target sets from ITS OWN
/// <c>purchasableProductIds</c> build in <c>BuildCardModelAsync</c> — a copy distinct from the two
/// application-service copies — so this is the only test that would catch that copy drifting out of
/// sync with the other two.</para>
/// </summary>
public sealed class RecipeDetailProducedExclusionTests(RecipeDetailProducedExclusionFactory factory)
    : IClassFixture<RecipeDetailProducedExclusionFactory>
{
    private static readonly HtmlParser Parser = new();

    [Fact(DisplayName = "'Add all ingredients' and 'Add missing' both count only the purchasable (non-produced) ingredient (plantry-4osq)")]
    public async Task Both_Buttons_Exclude_The_Produced_Ingredient()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());

        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);

        // Both Pasta (ordinary, missing) and Garden Tomatoes (produced, would-be-missing) render as
        // ingredient rows — exclusion only affects the shopping-list target sets, not the row itself.
        Assert.Contains("Rigatoni", html, StringComparison.Ordinal);
        Assert.Contains("Garden Tomatoes", html, StringComparison.Ordinal);

        var allBtn = doc.QuerySelectorAll("button[hx-post]")
            .FirstOrDefault(b => b.GetAttribute("hx-post")?.Contains("handler=AddAll") == true)
            ?? throw new InvalidOperationException("'Add all ingredients' button not found.");
        var missingBtn = doc.QuerySelectorAll("button[hx-post]")
            .FirstOrDefault(b => b.GetAttribute("hx-post")?.Contains("handler=AddMissing") == true)
            ?? throw new InvalidOperationException("'Add missing' button not found.");

        // If the produced exclusion regressed (in ANY of the three copies — the two application
        // services or this page model's own BuildCardModelAsync build), PendingLines would read 2
        // (Pasta + Garden Tomatoes) and these labels would read "Add 2 ...".
        Assert.Contains("Add 1 ingredient to shopping list", allBtn.TextContent, StringComparison.Ordinal);
        Assert.Contains("Add 1 missing to shopping list", missingBtn.TextContent, StringComparison.Ordinal);
    }
}

/// <summary>
/// Pasta (ordinary, tracked, no stock → Missing) and Garden Tomatoes (tracked, HOME-PRODUCED, no
/// stock → would also be Missing) — the minimal produced-exclusion scenario (plantry-4osq).
/// </summary>
public sealed class RecipeDetailProducedExclusionFactory : RecipeDetailFragmentFactory
{
    protected override Recipe BuildRecipe() => RecipeDetailFixture.BuildWithProducedIngredient();

    protected override IReadOnlyDictionary<Guid, CatalogProduct> Products =>
        RecipeDetailFixture.ProductsWithGardenTomatoes();

    protected override IReadOnlySet<Guid> ProducedProductIds =>
        new HashSet<Guid> { RecipeDetailFixture.GardenTomatoesId };

    // Neither ingredient has a stock record → both would read Missing at the fulfillment layer,
    // isolating the produced-exclusion as the only thing separating their button-count contribution.
    protected override IReadOnlyDictionary<Guid, ProductStock> Stock => new Dictionary<Guid, ProductStock>();

    protected override IReadOnlyDictionary<Guid, PricePoint> Prices => RecipeDetailFixture.PricesNone();
}
