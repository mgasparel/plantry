using Plantry.Inventory.Application;
using Plantry.Web.Pages.Shared;
using PantryPage = Plantry.Web.Pages.Pantry.IndexModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Safety-net unit tests for the pantry Expiry cell hybrid (plantry-fdoq): <see cref="ExpiryTone"/> — which
/// already bakes in the per-household "expiring soon" horizon — decides whether a row is <i>actionable</i>
/// (Expired/Soon → the unified <c>.badge-expiry</c> pill with wording+tier from <c>ExpiryDisplay</c>), calm
/// (Ok → muted absolute <c>d MMM</c> date), or undated (None → "—"). Acceptance criterion #3. Pins the
/// tone→cell-kind decision directly; the wording/tier logic it delegates to is covered by
/// <c>ExpiryDisplayTests</c>.
/// </summary>
public sealed class PantryExpiryCellTests
{
    private static readonly DateOnly Today = new(2026, 7, 20);

    private static PantryListItem Item(ExpiryTone tone, DateOnly? expiry) =>
        new(
            ProductId: Guid.NewGuid(),
            Name: "Test",
            CategoryName: null,
            LocationDisplay: null,
            IsVariant: false,
            TotalQuantity: 1m,
            DisplayUnitCode: "ea",
            LotCount: 1,
            SoonestExpiry: expiry,
            ExpiryTone: tone);

    [Fact(DisplayName = "None (no dated lots) → muted em dash, regardless of date")]
    public void None_RendersMutedDash()
    {
        var cell = PantryPage.ExpiryCell(Item(ExpiryTone.None, expiry: null), Today);

        Assert.Equal(GridCellKind.Muted, cell.Kind);
        Assert.Equal("—", cell.Value);
    }

    [Fact(DisplayName = "Expired → the unified urgent expiry pill with relative wording")]
    public void Expired_RendersUrgentPill()
    {
        var cell = PantryPage.ExpiryCell(Item(ExpiryTone.Expired, Today.AddDays(-2)), Today);

        Assert.Equal(GridCellKind.ExpiryBadge, cell.Kind);
        Assert.Equal("urgent", cell.ExpiryTier);
        Assert.Equal("Expired 2d ago", cell.Value);
    }

    [Fact(DisplayName = "Soon within 3 days → the soon-tier expiry pill")]
    public void Soon_NearTerm_RendersSoonPill()
    {
        var cell = PantryPage.ExpiryCell(Item(ExpiryTone.Soon, Today.AddDays(3)), Today);

        Assert.Equal(GridCellKind.ExpiryBadge, cell.Kind);
        Assert.Equal("soon", cell.ExpiryTier);
        Assert.Equal("in 3d", cell.Value);
    }

    [Fact(DisplayName = "Soon but >3 days out (in-horizon) → a pill, coloured calm 'ok' by design")]
    public void Soon_WithinHorizonButCalm_RendersOkTonedPill()
    {
        // ExpiryTone.Soon = within the household horizon; the pill's COLOUR tier still tracks urgency, so a
        // 5-day-out in-horizon item shows a pill (actionable) with the calm ok colour — the intended hybrid.
        var cell = PantryPage.ExpiryCell(Item(ExpiryTone.Soon, Today.AddDays(5)), Today);

        Assert.Equal(GridCellKind.ExpiryBadge, cell.Kind);
        Assert.Equal("ok", cell.ExpiryTier);
        Assert.Equal("in 5d", cell.Value);
    }

    [Fact(DisplayName = "Ok (beyond horizon) → muted absolute date, no pill")]
    public void Ok_RendersMutedAbsoluteDate()
    {
        var expiry = new DateOnly(2026, 8, 15);
        var cell = PantryPage.ExpiryCell(Item(ExpiryTone.Ok, expiry), Today);

        Assert.Equal(GridCellKind.Muted, cell.Kind);
        // Same "d MMM" formatting the cell uses — compared culture-agnostically so the test does not depend
        // on the ambient host culture's month abbreviation.
        Assert.Equal(expiry.ToString("d MMM"), cell.Value);
    }
}
