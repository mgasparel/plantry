using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Recipes.Domain;

public sealed class CookEventTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly RecipeId Recipe = RecipeId.New();
    private static readonly Guid User = Guid.CreateVersion7();

    private static readonly DateTimeOffset FixedNow = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static readonly IClock Clock = new FixedClock(FixedNow);

    // ── Record factory — happy path ───────────────────────────────────────────

    [Fact]
    public void Record_Sets_All_Fields()
    {
        var result = CookEvent.Record(Recipe, Household, servingsCooked: 4, User, Clock);

        Assert.True(result.IsSuccess);
        var evt = result.Value;
        Assert.Equal(Household, evt.HouseholdId);
        Assert.Equal(Recipe, evt.RecipeId);
        Assert.Equal(4, evt.ServingsCooked);
        Assert.Equal(User, evt.CookedBy);
        Assert.Equal(FixedNow, evt.CookedAt);
        Assert.NotEqual(Guid.Empty, evt.Id.Value);
    }

    [Fact]
    public void Record_Mints_Unique_Id_Per_Call()
    {
        var a = CookEvent.Record(Recipe, Household, 2, User, Clock).Value;
        var b = CookEvent.Record(Recipe, Household, 2, User, Clock).Value;

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Record_With_One_Serving_Succeeds()
    {
        var result = CookEvent.Record(Recipe, Household, servingsCooked: 1, User, Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.ServingsCooked);
    }

    // ── R2 guard — servings_cooked >= 1 ──────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Record_Rejects_Servings_Below_One(int servings)
    {
        var result = CookEvent.Record(Recipe, Household, servings, User, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Recipes.InvalidServings", result.Error.Code);
    }

    // ── Append-only / no domain events on Record ──────────────────────────────

    [Fact]
    public void Record_Raises_No_Domain_Events()
    {
        // RecipeCooked is emitted by CookRecipe application service (P2-3c), not here.
        var evt = CookEvent.Record(Recipe, Household, 3, User, Clock).Value;

        Assert.Empty(evt.DomainEvents);
    }
}
