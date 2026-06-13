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
    private static readonly Guid EachId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid FridgeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly IReadOnlyDictionary<string, Guid> UnitIdByCode =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["L"] = LitreId };
    private static readonly IReadOnlyDictionary<Guid, string> ProductNameById =
        new Dictionary<Guid, string> { [MilkId] = "Milk" };
    private static readonly IReadOnlyDictionary<Guid, Guid?> ProductDefaultLocationById =
        new Dictionary<Guid, Guid?> { [MilkId] = FridgeId };
    private static readonly IReadOnlyDictionary<Guid, Guid> ProductDefaultUnitById =
        new Dictionary<Guid, Guid> { [MilkId] = LitreId };
    private static readonly IReadOnlyDictionary<Guid, int?> ProductDefaultDueDaysById =
        new Dictionary<Guid, int?> { [MilkId] = 7 };

    private static readonly DateOnly Today = new(2026, 6, 12);

    [Fact]
    public void Pending_with_suggestions_prefills_all_fields()
    {
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var (productId, productName, qty, unitId, locationId, price, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

        Assert.Equal(MilkId, productId);
        Assert.Equal("Milk", productName);
        Assert.Equal(2m, qty);
        Assert.Equal(LitreId, unitId);
        Assert.Equal(FridgeId, locationId);
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

        var (productId, _, qty, unitId, _, price, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

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

        var (productId, productName, _, _, _, _, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

        Assert.Null(productId);                     // phantom ID discarded — drawer won't pre-select
        Assert.Equal("Mystery Item", productName);  // name hint still shown in row summary
    }

    [Fact]
    public void Pending_matched_product_prefills_default_location()
    {
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk");

        var (_, _, _, _, locationId, _, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

        Assert.Equal(FridgeId, locationId);
    }

    [Fact]
    public void User_resolved_location_takes_priority_over_product_default()
    {
        var confirmedLocationId = Guid.NewGuid();
        var line = Line(LineStatus.Confirmed,
            productId: MilkId, quantity: 1m, unitId: LitreId, locationId: confirmedLocationId);

        var (_, _, _, _, locationId, _, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

        Assert.Equal(confirmedLocationId, locationId);
    }

    [Fact]
    public void Non_pending_line_ignores_suggestions()
    {
        var line = Line(LineStatus.Dismissed,
            suggestedProductId: MilkId, suggestedQuantity: 2m,
            suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var (productId, _, qty, unitId, locationId, price, _) =
            ReviewRowModel.ComputePrefill(line, UnitIdByCode, ProductNameById, ProductDefaultLocationById);

        Assert.Null(productId);
        Assert.Null(qty);
        Assert.Null(unitId);
        Assert.Null(locationId);
        Assert.Null(price);
    }

    // ── plantry-4ha: product-default prefill — unit, expiry ─────────────────────────────────────

    [Fact]
    public void Pending_matched_no_parser_unit_uses_product_default_unit()
    {
        // Receipt parser detected no unit label — product default unit should fill in.
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: null /* no receipt unit */);

        var (_, _, _, unitId, _, _, _) = ReviewRowModel.ComputePrefill(
            line, UnitIdByCode, ProductNameById, ProductDefaultLocationById,
            ProductDefaultUnitById, ProductDefaultDueDaysById, Today);

        Assert.Equal(LitreId, unitId); // product default unit fills in when parser had none
    }

    [Fact]
    public void Pending_matched_with_parser_unit_receipt_unit_wins()
    {
        // Receipt parser detected a unit label ("L") — it must win over the product default,
        // even if the product's default happens to be the same unit.
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L" /* receipt-parsed */);

        var (_, _, _, unitId, _, _, _) = ReviewRowModel.ComputePrefill(
            line, UnitIdByCode, ProductNameById, ProductDefaultLocationById,
            ProductDefaultUnitById, ProductDefaultDueDaysById, Today);

        // Receipt unit wins — unitId comes from the parser label, not the product default.
        Assert.Equal(LitreId, unitId);
    }

    [Fact]
    public void Pending_matched_with_default_due_days_computes_expiry()
    {
        // Milk has DefaultDueDays = 7; expiry should be today + 7.
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L");

        var (_, _, _, _, _, _, expiry) = ReviewRowModel.ComputePrefill(
            line, UnitIdByCode, ProductNameById, ProductDefaultLocationById,
            ProductDefaultUnitById, ProductDefaultDueDaysById, Today);

        Assert.Equal(Today.AddDays(7), expiry);
    }

    [Fact]
    public void Pending_matched_no_default_due_days_expiry_is_null()
    {
        // Product has no DefaultDueDays — expiry should be null.
        var noDueDaysDueDaysById = new Dictionary<Guid, int?> { [MilkId] = null };
        var line = Line(LineStatus.Pending,
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L");

        var (_, _, _, _, _, _, expiry) = ReviewRowModel.ComputePrefill(
            line, UnitIdByCode, ProductNameById, ProductDefaultLocationById,
            ProductDefaultUnitById, noDueDaysDueDaysById, Today);

        Assert.Null(expiry);
    }

    [Fact]
    public void User_resolved_expiry_takes_priority_over_product_default()
    {
        var resolvedExpiry = new DateOnly(2026, 8, 1);
        var line = Line(LineStatus.Confirmed,
            productId: MilkId, quantity: 1m, unitId: LitreId,
            expiryDate: resolvedExpiry);

        var (_, _, _, _, _, _, expiry) = ReviewRowModel.ComputePrefill(
            line, UnitIdByCode, ProductNameById, ProductDefaultLocationById,
            ProductDefaultUnitById, ProductDefaultDueDaysById, Today);

        Assert.Equal(resolvedExpiry, expiry);
    }

    private static ReviewLineView Line(
        LineStatus status,
        Guid? productId = null,
        decimal? quantity = null,
        Guid? unitId = null,
        Guid? locationId = null,
        DateOnly? expiryDate = null,
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
            LocationId: locationId,
            ExpiryDate: expiryDate,
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
