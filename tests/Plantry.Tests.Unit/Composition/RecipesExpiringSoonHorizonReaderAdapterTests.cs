using Plantry.Tests.Unit.Inventory.Application;
using Plantry.Web.Recipes;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 test for the Recipes <see cref="ExpiringSoonHorizonReaderAdapter"/> (plantry-riqy,
/// plantry-5yhd) — a pure delegate onto Inventory's <see cref="Plantry.Pantry.Application.IExpiringSoonHorizon"/>,
/// the single source of truth for the per-household "expiring soon" horizon, so the Recipes browse
/// "use soon" filter resolves the exact same value as Inventory's Today widget. Same-shape adapter as
/// <c>Plantry.Web.MealPlanning.ExpiringSoonHorizonReaderAdapter</c>, already covered elsewhere; here we
/// pin the Recipes-side wiring.
/// </summary>
public sealed class RecipesExpiringSoonHorizonReaderAdapterTests
{
    [Fact(DisplayName = "GetDaysAsync forwards Inventory's configured horizon verbatim")]
    public async Task Forwards_Horizon_Verbatim()
    {
        var adapter = new ExpiringSoonHorizonReaderAdapter(new FakeExpiringSoonHorizon(days: 10));

        var days = await adapter.GetDaysAsync();

        Assert.Equal(10, days);
    }
}
