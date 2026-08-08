using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Web.Pages.Intake;
using Xunit;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 unit tests for <see cref="IntakeLinePriceDeltas.Compute"/> (plantry-bb7p) — the pure combination of
/// pre-fetched current/last unit prices into a per-line percentage delta. All Market-port calls are
/// simulated by the caller-supplied dictionaries, so these tests pin the combination rules without any
/// pricing infrastructure: only-Confirmed, both-sides-present ("confidently unit-normalizable"), and the
/// percentage-change arithmetic itself.
/// </summary>
public sealed class IntakeLinePriceDeltasTests
{
    private static ReviewLineView Line(LineStatus status, Guid? productId) => new(
        LineId: Guid.NewGuid(),
        LineNo: 1,
        ReceiptText: "SOME ITEM",
        SuggestedConfidence: SuggestedConfidence.High,
        Status: status,
        ProductId: productId,
        SkuId: null,
        Quantity: 1m,
        UnitId: Guid.NewGuid(),
        LocationId: null,
        ExpiryDate: null,
        Price: 4.50m,
        IsNewProduct: false,
        NewProductName: null,
        NewProductCategoryId: null,
        SuggestedProductId: null,
        SuggestedProductName: null,
        SuggestedQuantity: null,
        SuggestedUnitLabel: null,
        SuggestedPrice: null);

    [Fact(DisplayName = "A Confirmed line with both a current and a last unit price gets its percentage delta")]
    public void Confirmed_Line_With_Both_Prices_Gets_Delta()
    {
        var productId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed, productId);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 2.24m };
        var last = new Dictionary<Guid, decimal> { [productId] = 2.00m };

        var result = IntakeLinePriceDeltas.Compute([line], current, last);

        Assert.Equal(0.12m, result[line.LineId]);
    }

    [Fact(DisplayName = "A price drop yields a negative delta")]
    public void Price_Drop_Yields_Negative_Delta()
    {
        var productId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed, productId);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 1.84m };
        var last = new Dictionary<Guid, decimal> { [productId] = 2.00m };

        var result = IntakeLinePriceDeltas.Compute([line], current, last);

        Assert.Equal(-0.08m, result[line.LineId]);
    }

    [Theory(DisplayName = "A non-Confirmed line never gets a delta, even with both prices available")]
    [InlineData(LineStatus.Pending)]
    [InlineData(LineStatus.Dismissed)]
    [InlineData(LineStatus.Committed)]
    public void Non_Confirmed_Line_Never_Gets_A_Delta(LineStatus status)
    {
        var productId = Guid.NewGuid();
        var line = Line(status, productId);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 2.24m };
        var last = new Dictionary<Guid, decimal> { [productId] = 2.00m };

        var result = IntakeLinePriceDeltas.Compute([line], current, last);

        Assert.False(result.ContainsKey(line.LineId));
    }

    [Fact(DisplayName = "No entry when the line's own current unit price never normalized")]
    public void Missing_Current_Unit_Price_Omits_The_Line()
    {
        var productId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed, productId);
        var last = new Dictionary<Guid, decimal> { [productId] = 2.00m };

        var result = IntakeLinePriceDeltas.Compute([line], new Dictionary<Guid, decimal>(), last);

        Assert.False(result.ContainsKey(line.LineId));
    }

    [Fact(DisplayName = "No entry when the product has no prior purchase unit price")]
    public void Missing_Last_Unit_Price_Omits_The_Line()
    {
        var productId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed, productId);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 2.24m };

        var result = IntakeLinePriceDeltas.Compute([line], current, new Dictionary<Guid, decimal>());

        Assert.False(result.ContainsKey(line.LineId));
    }

    [Fact(DisplayName = "No entry for a Confirmed line with no resolved product id")]
    public void Missing_ProductId_Omits_The_Line()
    {
        var line = Line(LineStatus.Confirmed, productId: null);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 2.24m };
        var last = new Dictionary<Guid, decimal>();

        var result = IntakeLinePriceDeltas.Compute([line], current, last);

        Assert.False(result.ContainsKey(line.LineId));
    }

    [Fact(DisplayName = "A zero/negative last unit price never divides — the line is omitted, not a divide-by-zero")]
    public void Nonpositive_Last_Unit_Price_Omits_The_Line()
    {
        var productId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed, productId);
        var current = new Dictionary<Guid, decimal> { [line.LineId] = 2.24m };
        var last = new Dictionary<Guid, decimal> { [productId] = 0m };

        var result = IntakeLinePriceDeltas.Compute([line], current, last);

        Assert.False(result.ContainsKey(line.LineId));
    }
}
