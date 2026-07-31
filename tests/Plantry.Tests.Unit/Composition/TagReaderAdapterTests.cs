using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Unit.Recipes.Application;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="TagReaderAdapter"/> (plantry-riqy, DM-20) — the MealPlanning→Recipes ACL
/// adapter that groups the tag vocabulary into the four canonical categories plus an "Uncategorized"
/// bucket. Covers canonical-order grouping with hues, alphabetical ordering within a group, the
/// null-category fallthrough, and the active-only filter (archived tags excluded).
/// </summary>
public sealed class TagReaderAdapterTests
{
    private static readonly HouseholdId Household = HouseholdId.New();

    private static Tag NewTag(string name, TagCategory? category) =>
        Tag.Create(Household, name, category, SystemClock.Instance);

    [Fact(DisplayName = "ListGroupedAsync emits groups in canonical Diet/Protein/Flavor/Cuisine order with the correct hue")]
    public async Task Emits_Canonical_Groups_With_Hue()
    {
        var repo = new FakeTagRepository();
        repo.Items.Add(NewTag("Thai", TagCategory.Cuisine));
        repo.Items.Add(NewTag("Spicy", TagCategory.Flavor));
        repo.Items.Add(NewTag("Chicken", TagCategory.Protein));
        repo.Items.Add(NewTag("Vegan", TagCategory.Diet));

        var groups = await new TagReaderAdapter(repo).ListGroupedAsync();

        Assert.Equal(["Diet", "Protein", "Flavor", "Cuisine"], groups.Select(g => g.Category));
        Assert.Equal([150, 28, 330, 255], groups.Select(g => g.CategoryHue));
    }

    [Fact(DisplayName = "ListGroupedAsync orders tags within a group alphabetically")]
    public async Task Orders_Tags_Within_Group_Alphabetically()
    {
        // PreserveInsertionOrder: the fake must hand the adapter Vegan→Keto (reverse-alphabetical)
        // so the asserted ["Keto", "Vegan"] can only come from the adapter's own OrderBy.
        var repo = new FakeTagRepository { PreserveInsertionOrder = true };
        repo.Items.Add(NewTag("Vegan", TagCategory.Diet));
        repo.Items.Add(NewTag("Keto", TagCategory.Diet));

        var groups = await new TagReaderAdapter(repo).ListGroupedAsync();

        var diet = Assert.Single(groups);
        Assert.Equal(["Keto", "Vegan"], diet.Tags.Select(t => t.Name));
    }

    [Fact(DisplayName = "ListGroupedAsync places a null-category tag into the Uncategorized bucket with a null hue")]
    public async Task Places_NullCategory_Tag_In_Uncategorized()
    {
        // PreserveInsertionOrder: the fake hands the adapter Zesty→Family Favourite
        // (reverse-alphabetical) so the asserted order can only come from the Uncategorized
        // bucket's own OrderBy — the adapter's second, independent sort site.
        var repo = new FakeTagRepository { PreserveInsertionOrder = true };
        repo.Items.Add(NewTag("Zesty", null));
        repo.Items.Add(NewTag("Family Favourite", null));

        var groups = await new TagReaderAdapter(repo).ListGroupedAsync();

        var group = Assert.Single(groups);
        Assert.Equal("Uncategorized", group.Category);
        Assert.Null(group.CategoryHue);
        Assert.Equal(["Family Favourite", "Zesty"], group.Tags.Select(t => t.Name));
        Assert.All(group.Tags, t => Assert.Null(t.CategoryHue));
    }

    [Fact(DisplayName = "ListGroupedAsync excludes archived tags")]
    public async Task Excludes_Archived_Tags()
    {
        var archived = NewTag("Retired", TagCategory.Diet);
        archived.Archive(SystemClock.Instance);
        var repo = new FakeTagRepository();
        repo.Items.Add(archived);

        var groups = await new TagReaderAdapter(repo).ListGroupedAsync();

        Assert.Empty(groups);
    }
}
