using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Web.Pages.Intake;

namespace Plantry.Tests.Web;

/// <summary>
/// L1 tests for <see cref="ReviewRowModel.ComputePrefill"/> — the prefill priority logic that
/// surfaces AI suggestions as form defaults while ensuring user-resolved fields always win.
/// </summary>
public sealed class ReviewRowModelTests
{
    private static readonly Guid MilkId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LitreId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly IReadOnlyDictionary<string, Guid> UnitIdByCode =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["L"] = LitreId };
    private static readonly IReadOnlyDictionary<Guid, string> ProductNameById =
        new Dictionary<Guid, string> { [MilkId] = "Milk" };

    [Fact]
    public void Pending_with_suggestions_prefills_all_fields()
    {
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var (productId, productName, qty, unitId, price) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById);

        Assert.Equal(MilkId, productId);
        Assert.Equal("Milk", productName);
        Assert.Equal(2m, qty);
        Assert.Equal(LitreId, unitId);
        Assert.Equal(3.99m, price);
    }

    [Fact]
    public void User_resolved_fields_take_priority_over_suggestions()
    {
        var confirmedProductId = Guid.NewGuid();
        var confirmedUnitId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed,
            productId: confirmedProductId, quantity: 1m, unitId: confirmedUnitId, price: 5.00m,
            suggestedProductId: MilkId, suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var (productId, _, qty, unitId, price) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById);

        Assert.Equal(confirmedProductId, productId);
        Assert.Equal(1m, qty);
        Assert.Equal(confirmedUnitId, unitId);
        Assert.Equal(5.00m, price);
    }

    [Fact]
    public void Pending_with_phantom_product_id_drops_id_but_keeps_name()
    {
        var phantomId = Guid.NewGuid(); // not present in ProductNameById
        var line = Line(LineStatus.Pending,
            suggestedProductId: phantomId, suggestedProductName: "Mystery Item");

        var (productId, productName, _, _, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById);

        Assert.Null(productId);                     // phantom ID discarded — drawer won't pre-select
        Assert.Equal("Mystery Item", productName);  // name hint still shown in row summary
    }

    [Fact]
    public void Non_pending_line_ignores_suggestions()
    {
        var line = Line(LineStatus.Dismissed,
            suggestedProductId: MilkId, suggestedQuantity: 2m,
            suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var (productId, _, qty, unitId, price) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById);

        Assert.Null(productId);
        Assert.Null(qty);
        Assert.Null(unitId);
        Assert.Null(price);
    }

    private static ReviewLineView Line(
        LineStatus status,
        Guid? productId = null,
        decimal? quantity = null,
        Guid? unitId = null,
        decimal? price = null,
        Guid? suggestedProductId = null,
        string? suggestedProductName = null,
        decimal? suggestedQuantity = null,
        string? suggestedUnitLabel = null,
        decimal? suggestedPrice = null) =>
        new(
            LineId: Guid.NewGuid(),
            LineNo: 1,
            ReceiptText: "RECEIPT LINE",
            SuggestedConfidence: SuggestedConfidence.High,
            Status: status,
            ProductId: productId,
            SkuId: null,
            Quantity: quantity,
            UnitId: unitId,
            LocationId: null,
            ExpiryDate: null,
            Price: price,
            IsNewProduct: false,
            NewProductName: null,
            NewProductCategoryId: null,
            SuggestedProductId: suggestedProductId,
            SuggestedProductName: suggestedProductName,
            SuggestedQuantity: suggestedQuantity,
            SuggestedUnitLabel: suggestedUnitLabel,
            SuggestedPrice: suggestedPrice);
}
