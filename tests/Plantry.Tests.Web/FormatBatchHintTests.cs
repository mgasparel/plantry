using Plantry.Web.Pages.Recipes;

namespace Plantry.Tests.Web;

/// <summary>
/// Pins <see cref="DetailsModel.FormatBatchHint"/> after it was re-implemented on
/// <c>QuantityDisplay.FormatAmount</c> (plantry-vci8.3, quantity-display.md §1/§7 — "glyph logic lives
/// once"). The acceptance criterion is that the batch-hint output for the common cases is <b>unchanged</b>:
/// proper-fraction batches keep the singular "½ batch" suffix, one batch is "1 batch", and only mixed/whole
/// batches above one pluralise. A pure static method — L1, no host needed.
/// </summary>
public sealed class FormatBatchHintTests
{
    [Theory]
    // Proper-fraction batches: glyph + singular "batch" (unchanged from the pre-refactor switch).
    [InlineData(1, 2, "½ batch")]   // 2 servings of a 4-serving sub
    [InlineData(1, 4, "¼ batch")]
    [InlineData(3, 4, "¾ batch")]
    [InlineData(1, 3, "⅓ batch")]
    [InlineData(2, 3, "⅔ batch")]
    // Exactly one batch: singular.
    [InlineData(4, 4, "1 batch")]
    // Above one: mixed number / whole, plural (FormatAmount now renders the mixed number "1½").
    [InlineData(6, 4, "1½ batches")]
    [InlineData(8, 4, "2 batches")]
    // Snap-to-one band: 126/125 = 1.008 renders "1" (remainder ≤ 0.01 snaps down), so the suffix must be
    // singular — pluralising off the raw ratio would give the contradictory "1 batches".
    [InlineData(126, 125, "1 batch")]
    public void FormatsBatchHint(int servings, int subDefaultServings, string expected) =>
        Assert.Equal(expected, DetailsModel.FormatBatchHint(servings, subDefaultServings));

    [Theory]
    [InlineData(0, 4)]   // zero servings → no hint
    [InlineData(2, 0)]   // no sub default → no hint
    public void EmptyWhenNotComputable(int servings, int subDefaultServings) =>
        Assert.Equal("", DetailsModel.FormatBatchHint(servings, subDefaultServings));
}
