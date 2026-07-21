using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Web.Pages.Shared;
using PantryProductPage = Plantry.Web.Pages.Pantry.Products.DetailModel;

namespace Plantry.Tests.Web.Grids;

/// <summary>
/// Safety-net unit tests for the pantry History grid's Source cell (receipt-intake-history.md H11): a
/// resolved chip renders <see cref="GridCellKind.SourceChip"/> with the right icon; everything else keeps
/// the pre-existing plain-text/muted fallback. Mirrors <c>PantryExpiryCellTests</c>'s pattern of pinning
/// the pure cell-selection function directly rather than through a full page render.
/// </summary>
public sealed class StockDetailSourceCellTests
{
    private static StockJournalRow Row(Guid journalId, StockSourceType? sourceType, Guid? sourceRef) =>
        new(journalId, 1m, "ea", StockReason.Purchase, sourceType, sourceRef, DateTimeOffset.UtcNow);

    // A stub hrefFor stands in for the Razor call site's Url.Page-backed builder (plantry-72c6):
    // SourceCell itself stays pure and doesn't know how a chip's ids turn into a URL.
    private static string StubHrefFor(ProvenanceChip chip) => $"stub://{chip.Kind}/{chip.TargetId}/{chip.LineAnchorId}";

    [Fact(DisplayName = "Resolved Intake row → a SourceChip cell with the receipt icon")]
    public void ResolvedIntake_RendersReceiptChip()
    {
        var journalId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var lineAnchorId = Guid.NewGuid();
        var chips = new Dictionary<Guid, ProvenanceChip>
        {
            [journalId] = new(ProvenanceChipKind.Intake, "Costco · 18 Jul", targetId, lineAnchorId),
        };

        var cell = PantryProductPage.SourceCell(Row(journalId, StockSourceType.Intake, Guid.NewGuid()), chips, StubHrefFor);

        Assert.Equal(GridCellKind.SourceChip, cell.Kind);
        Assert.Equal(SourceChipIcon.Receipt, cell.ChipIcon);
        Assert.Equal("Costco · 18 Jul", cell.Value);
        Assert.Equal($"stub://Intake/{targetId}/{lineAnchorId}", cell.Url);
    }

    [Fact(DisplayName = "Resolved Cook row → a SourceChip cell with the chef-hat icon")]
    public void ResolvedCook_RendersChefHatChip()
    {
        var journalId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();
        var chips = new Dictionary<Guid, ProvenanceChip>
        {
            [journalId] = new(ProvenanceChipKind.Cook, "Shakshuka", recipeId),
        };

        var cell = PantryProductPage.SourceCell(Row(journalId, StockSourceType.Cook, Guid.NewGuid()), chips, StubHrefFor);

        Assert.Equal(GridCellKind.SourceChip, cell.Kind);
        Assert.Equal(SourceChipIcon.Cook, cell.ChipIcon);
        Assert.Equal("Shakshuka", cell.Value);
        Assert.Equal($"stub://Cook/{recipeId}/", cell.Url);
    }

    [Fact(DisplayName = "Manual row → plain text, never a chip")]
    public void Manual_RendersPlainText()
    {
        var cell = PantryProductPage.SourceCell(Row(Guid.NewGuid(), StockSourceType.Manual, null), new Dictionary<Guid, ProvenanceChip>(), StubHrefFor);

        Assert.Equal(GridCellKind.Text, cell.Kind);
        Assert.Equal("Manual", cell.Value);
    }

    [Fact(DisplayName = "Unresolved Intake row (no chip in the dictionary) → plain text fallback")]
    public void UnresolvedIntake_FallsBackToPlainText()
    {
        var cell = PantryProductPage.SourceCell(
            Row(Guid.NewGuid(), StockSourceType.Intake, Guid.NewGuid()), new Dictionary<Guid, ProvenanceChip>(), StubHrefFor);

        Assert.Equal(GridCellKind.Text, cell.Kind);
        Assert.Equal("Intake", cell.Value);
    }

    [Fact(DisplayName = "No SourceType at all (pre-DM14 legacy row) → muted em dash")]
    public void NoSourceType_RendersMutedDash()
    {
        var cell = PantryProductPage.SourceCell(Row(Guid.NewGuid(), null, null), new Dictionary<Guid, ProvenanceChip>(), StubHrefFor);

        Assert.Equal(GridCellKind.Muted, cell.Kind);
        Assert.Equal("—", cell.Value);
    }

    // <see cref="PantryProductPage.HrefFor"/> — the Url.Page-backed builder consumed at the Razor call
    // site — is exercised end-to-end (real routing, not a hand-rolled IUrlHelper) by the rendered-HTML
    // assertions in Plantry.Tests.Web.Pantry.ProvenanceChipHrefTests (plantry-72c6).
}
