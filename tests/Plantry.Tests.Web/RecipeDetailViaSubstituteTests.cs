using System.Net;
using AngleSharp.Html.Parser;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 render test for the Detail page's "in stock via substitute" display touchpoint (plantry-aqpa.5) —
/// where fulfillment reads a line as <see cref="IngredientStatus.InStockViaSubstitute"/>, the
/// substitute product's name must be visible in the row (display only, no authoring UI here; that
/// lives on the substitute product's own Catalog detail page). Garlic (an existing fixture ingredient,
/// 3 ea required) has zero direct stock; Garlic Powder — a product that is NOT itself a recipe
/// ingredient — has ample stock and one substitution edge (target: Garlic, substitute: Garlic Powder).
/// </summary>
public sealed class RecipeDetailViaSubstituteTests(RecipeDetailViaSubstituteFactory factory)
    : IClassFixture<RecipeDetailViaSubstituteFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetGarlicRowHtmlAsync(RecipeDetailViaSubstituteFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, RecipeDetailFixture.HouseholdAId.ToString());
        var response = await client.GetAsync($"/Recipes/{f.RecipeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pageHtml = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(pageHtml);
        var garlicRow = doc.QuerySelectorAll(".rd-ing-list .rd-ing-row")
            .FirstOrDefault(row => row.QuerySelector(".rd-ing-name")?.TextContent.Contains("Garlic Cloves") == true)
            ?? throw new InvalidOperationException("Garlic ingredient row not found in the rendered Detail page.");
        return garlicRow.OuterHtml;
    }

    [Fact(DisplayName = "InStockViaSubstitute row names the substitute product, not the generic label")]
    public async Task ViaSubstitute_Row_Names_The_Substitute()
    {
        var rowHtml = await GetGarlicRowHtmlAsync(factory);

        Assert.Contains("In your pantry (via Garlic Powder)", rowHtml, StringComparison.Ordinal);
        // Proves the name actually reached the partial rather than the pre-plantry-aqpa.5 generic
        // fallback wording.
        Assert.DoesNotContain("(via substitute)", rowHtml, StringComparison.Ordinal);
        Assert.Contains("rd-ing-sub--viasub", rowHtml, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "InStockViaSubstitute row names every contributing substitute, comma-joined")]
    public async Task ViaSubstitute_Row_Names_Multiple_Substitutes()
    {
        using var twoSubstitutesFactory = new RecipeDetailTwoSubstitutesFactory();
        var rowHtml = await GetGarlicRowHtmlAsync(twoSubstitutesFactory);

        Assert.Contains("In your pantry (via Garlic Powder, Roasted Garlic)", rowHtml, StringComparison.Ordinal);
    }
}

/// <summary>
/// Garlic (recipe ingredient, needs 3 ea) has zero direct stock; Garlic Powder (a product that is NOT
/// itself a recipe ingredient) has ample stock and one substitution edge naming it as Garlic's
/// substitute — the minimal single-substitute InStockViaSubstitute scenario.
/// </summary>
public class RecipeDetailViaSubstituteFactory : RecipeDetailFragmentFactory
{
    public static readonly Guid GarlicPowderId = Guid.Parse("22222222-2222-2222-2222-2222222222aa");

    protected override IReadOnlyDictionary<Guid, CatalogProduct> Products =>
        new Dictionary<Guid, CatalogProduct>(RecipeDetailFixture.Products())
        {
            [GarlicPowderId] = new(GarlicPowderId, "Garlic Powder", TrackStock: true, RecipeDetailFixture.EachUnitId, null, false, []),
        };

    protected override IReadOnlyDictionary<Guid, ProductStock> Stock =>
        new Dictionary<Guid, ProductStock>(RecipeDetailFixture.Stock(Today))
        {
            [RecipeDetailFixture.GarlicId] = new(RecipeDetailFixture.GarlicId, 0m, RecipeDetailFixture.EachUnitId, SoonestExpiry: null),
            [GarlicPowderId] = new(GarlicPowderId, 10m, RecipeDetailFixture.EachUnitId, SoonestExpiry: null),
        };

    protected override ISubstitutionReader SubstitutionReader
    {
        get
        {
            var repo = new FakeSubstitutionRepository();
            repo.Items.Add(Substitution.Create(
                HouseholdId.From(RecipeDetailFixture.HouseholdAId),
                targetProductId: RecipeDetailFixture.GarlicId, targetQuantity: 1m, targetUnitId: RecipeDetailFixture.EachUnitId,
                substituteProductId: GarlicPowderId, substituteQuantity: 1m, substituteUnitId: RecipeDetailFixture.EachUnitId,
                SystemClock.Instance));
            return new FakeSubstitutionReader(repo);
        }
    }
}

/// <summary>
/// As <see cref="RecipeDetailViaSubstituteFactory"/>, but with a SECOND edge (Roasted Garlic) also
/// contributing to the same short Garlic line — the multi-substitute comma-joined naming path.
/// </summary>
public sealed class RecipeDetailTwoSubstitutesFactory : RecipeDetailViaSubstituteFactory
{
    private static readonly Guid RoastedGarlicId = Guid.Parse("22222222-2222-2222-2222-2222222222bb");

    protected override IReadOnlyDictionary<Guid, CatalogProduct> Products =>
        new Dictionary<Guid, CatalogProduct>(base.Products)
        {
            [RoastedGarlicId] = new(RoastedGarlicId, "Roasted Garlic", TrackStock: true, RecipeDetailFixture.EachUnitId, null, false, []),
        };

    protected override IReadOnlyDictionary<Guid, ProductStock> Stock =>
        new Dictionary<Guid, ProductStock>(base.Stock)
        {
            [RoastedGarlicId] = new(RoastedGarlicId, 5m, RecipeDetailFixture.EachUnitId, SoonestExpiry: null),
        };

    protected override ISubstitutionReader SubstitutionReader
    {
        get
        {
            var repo = new FakeSubstitutionRepository();
            repo.Items.Add(Substitution.Create(
                HouseholdId.From(RecipeDetailFixture.HouseholdAId),
                targetProductId: RecipeDetailFixture.GarlicId, targetQuantity: 1m, targetUnitId: RecipeDetailFixture.EachUnitId,
                substituteProductId: GarlicPowderId, substituteQuantity: 1m, substituteUnitId: RecipeDetailFixture.EachUnitId,
                SystemClock.Instance));
            repo.Items.Add(Substitution.Create(
                HouseholdId.From(RecipeDetailFixture.HouseholdAId),
                targetProductId: RecipeDetailFixture.GarlicId, targetQuantity: 1m, targetUnitId: RecipeDetailFixture.EachUnitId,
                substituteProductId: RoastedGarlicId, substituteQuantity: 1m, substituteUnitId: RecipeDetailFixture.EachUnitId,
                SystemClock.Instance));
            return new FakeSubstitutionReader(repo);
        }
    }
}
