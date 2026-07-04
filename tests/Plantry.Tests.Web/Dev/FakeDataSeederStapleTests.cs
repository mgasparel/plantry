using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Dev;

namespace Plantry.Tests.Web.Dev;

/// <summary>
/// Guards the seed-data fix for plantry-4udr: "to taste" staples (Sea salt / Black pepper) must be
/// minted as untracked so the seed recipes that reference them with a null quantity are saveable
/// through <c>AuthorRecipe</c> (R5). Covers the derived untracked-staple set and the fail-loud seed
/// guard — both pure/static so no DB or Aspire stack is required.
/// </summary>
public sealed class FakeDataSeederStapleTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private static readonly HouseholdId Household = HouseholdId.From(Guid.NewGuid());
    private static readonly UnitId Grams = UnitId.From(Guid.NewGuid());

    // ── Derived untracked-staple set ────────────────────────────────────────────────

    [Fact]
    public void UntrackedStapleNames_Is_Exactly_The_ToTaste_Staples()
    {
        // Derived from usage (products appearing only as null-quantity seed lines). On the current
        // seed data that is exactly Sea salt + Black pepper — the canonical untracked staples.
        Assert.True(FakeDataSeeder.UntrackedStapleNames.SetEquals(["Sea salt", "Black pepper"]),
            $"Expected {{Sea salt, Black pepper}} but got {{{string.Join(", ", FakeDataSeeder.UntrackedStapleNames)}}}");
    }

    [Theory]
    // Products used with a real quantity anywhere must stay tracked (excluded from the set).
    [InlineData("Olive oil")]   // always a real quantity ("to taste" in prose, but 30 g in the line)
    [InlineData("Onions")]
    [InlineData("Chicken breast")]
    [InlineData("Chickpeas")]
    public void Products_Used_With_A_Real_Quantity_Are_Not_Untracked(string name)
    {
        Assert.DoesNotContain(name, FakeDataSeeder.UntrackedStapleNames);
    }

    [Theory]
    [InlineData("Sea salt")]
    [InlineData("Black pepper")]
    public void ToTaste_Only_Staples_Are_Untracked(string name)
    {
        Assert.Contains(name, FakeDataSeeder.UntrackedStapleNames);
    }

    // ── Fail-loud seed guard (R5 invariant) ─────────────────────────────────────────

    [Fact]
    public void Guard_Throws_For_A_Tracked_Product_With_A_Null_Quantity_Naming_Product_And_Recipe()
    {
        // A tracked product minted with a "to taste" (null qty) line is exactly the defect: the seed
        // must fail fast rather than persist a recipe AuthorRecipe would reject on save.
        var tracked = Product.Create(Household, "Sea salt", Grams, Clock); // defaults trackStock: true

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FakeDataSeeder.AssertSeedLineSatisfiesR5(tracked, quantity: null, recipeName: "Broken Curry"));

        Assert.Contains("Sea salt", ex.Message);
        Assert.Contains("Broken Curry", ex.Message);
    }

    [Fact]
    public void Guard_Allows_An_Untracked_Product_With_A_Null_Quantity()
    {
        // The fixed state: an untracked staple with a "to taste" line is legal (R5 permits null
        // qty/unit only for an untracked product).
        var staple = Product.Create(Household, "Sea salt", Grams, Clock, trackStock: false);

        FakeDataSeeder.AssertSeedLineSatisfiesR5(staple, quantity: null, recipeName: "Chicken & Chickpea Curry");
    }

    [Fact]
    public void Guard_Allows_A_Tracked_Product_With_A_Real_Quantity()
    {
        var tracked = Product.Create(Household, "Onions", Grams, Clock);

        FakeDataSeeder.AssertSeedLineSatisfiesR5(tracked, quantity: 200m, recipeName: "Chicken & Chickpea Curry");
    }
}
