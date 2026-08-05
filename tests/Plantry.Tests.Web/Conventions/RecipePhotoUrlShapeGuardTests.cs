using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.Conventions;

/// <summary>
/// Source-scanning guard (plantry-8cl1) for the recipe-photo URL shape defect: two spots built the photo
/// URL as <c>/Recipes/Details?id={id}&amp;handler=Photo</c> — the recipe *page* route with a
/// <c>handler</c> query param — instead of the actual photo endpoint <c>/Recipes/{id}?handler=Photo</c>.
/// <c>Details.cshtml</c> declares <c>@page "/Recipes/{id:guid}"</c>; there is no <c>/Recipes/Details</c>
/// route, so the old URL never matched and the browser rendered a broken image.
///
/// <para><c>MealPlanSearchPhotoUrlTests</c> (in <c>MealPlanning/</c>) already exercises the server-side
/// half of the fix (<c>Index.cshtml.cs</c>'s <c>photoUrl</c> field) end-to-end through the running
/// handler. The client-side half — <c>meal-planner.js</c>'s dish-list thumbnail, which rebuilds the URL
/// itself rather than reading <c>photoUrl</c> from the server — has no rendering harness in this repo, so
/// it is guarded here by source text instead, following the same pattern as
/// <see cref="AlpineXDataRawGuardTests"/>. <see cref="WebSourceTree.EnumerateSourceFiles"/> deliberately
/// excludes <c>wwwroot</c> (vendored JS/CSS + client islands are a separate concern, plantry-2x6e.3), so
/// this guard walks <c>src/Plantry.Web</c> itself rather than reusing that helper, to also catch a future
/// reintroduction of the bad shape in any <c>.cs</c>/<c>.cshtml</c>/<c>.js</c> file — including
/// <c>wwwroot</c> — not just <c>meal-planner.js</c>.</para>
/// </summary>
public sealed class RecipePhotoUrlShapeGuardTests
{
    private const string BadUrlShape = "/Recipes/Details?id=";

    [Fact(DisplayName = "meal-planner.js's dish-list thumbnail uses the real photo endpoint, not the /Recipes/Details page route")]
    public void MealPlannerIsland_DishThumbnail_UsesRealPhotoEndpoint()
    {
        var path = Path.Combine(WebSourceTree.RepoRoot(), "src", "Plantry.Web", "wwwroot", "js", "islands", "meal-planner.js");
        var js = File.ReadAllText(path);

        Assert.Contains("\"/Recipes/\" + d.itemId + \"?handler=Photo\"", js);
        Assert.DoesNotContain(BadUrlShape, js);
    }

    [Fact(DisplayName = "No source file under Plantry.Web builds a recipe-photo URL with the broken /Recipes/Details?id= shape")]
    public void PlantryWeb_HasNoBrokenRecipeDetailsPhotoUrlShape()
    {
        var webRoot = Path.Combine(WebSourceTree.RepoRoot(), "src", "Plantry.Web");

        var offenders = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            // obj/bin hold generated/published build artifacts (e.g. bin/Release/.../wwwroot/js/meal-editor.js),
            // not source — a stale copy there is not something this guard can or should enforce.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p).Contains(BadUrlShape))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The recipe-photo endpoint is /Recipes/{id}?handler=Photo — there is no /Recipes/Details route " +
            "(Details.cshtml declares @page \"/Recipes/{id:guid}\"), so this shape 404s and renders a broken " +
            "image (plantry-8cl1). Offenders:\n" + string.Join("\n", offenders));
    }
}
