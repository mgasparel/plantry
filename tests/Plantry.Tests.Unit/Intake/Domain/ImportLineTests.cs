using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Domain;

public sealed class ImportLineTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid LocationId = Guid.CreateVersion7();
    private static readonly IClock Clock = SystemClock.Instance;

    private static ImportLine MakeLine(SuggestedConfidence confidence = SuggestedConfidence.High) =>
        MakeLineWithSuggestions(confidence);

    private static ImportLine MakeLineWithSuggestions(
        SuggestedConfidence confidence = SuggestedConfidence.High,
        Guid? suggestedProductId = null,
        string? suggestedProductName = null,
        decimal? suggestedQuantity = null,
        string? suggestedUnitLabel = null,
        decimal? suggestedPrice = null)
    {
        var session = ImportSession.Start(Household, ImportSourceType.Receipt, Guid.CreateVersion7(), Clock);
        return session.AddLine(1, "500g Flour", confidence, """{"qty":500}""",
            suggestedProductId, suggestedProductName, suggestedQuantity, suggestedUnitLabel, suggestedPrice);
    }

    [Fact]
    public void Weight_Each_Ground_Truth_Is_Set_At_Parse_And_Survives_Confirm()
    {
        var session = ImportSession.Start(Household, ImportSourceType.Receipt, Guid.CreateVersion7(), Clock);
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: ProductId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);

        Assert.True(line.HasEachEstimate);
        Assert.Equal(1.34m, line.ReceiptWeight);
        Assert.Equal("lb", line.ReceiptWeightUnitLabel);
        Assert.Equal(7m, line.EstimatedEachCount);
        Assert.Equal(SuggestedConfidence.High, line.EstimatedEachConfidence);

        // The user accepts the each-count: quantity=7 in the each unit. Ground truth must NOT be clobbered.
        var eachUnit = Guid.CreateVersion7();
        line.Confirm(ProductId, null, 7m, eachUnit, LocationId, expiryDate: null, price: 0.79m);

        Assert.Equal(7m, line.Quantity);          // user-resolved each-count
        Assert.Equal(eachUnit, line.UnitId);
        Assert.Equal(1.34m, line.ReceiptWeight);  // ground-truth weight preserved through confirm
        Assert.Equal("lb", line.ReceiptWeightUnitLabel);
        Assert.Equal(7m, line.EstimatedEachCount);
    }

    [Fact]
    public void Confirm_Sets_All_User_Resolved_Fields_And_Transitions_To_Confirmed()
    {
        var line = MakeLine();
        var expiry = new DateOnly(2027, 1, 1);

        var result = line.Confirm(ProductId, null, 500m, UnitId, LocationId, expiry, 2.99m);

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Confirmed, line.Status);
        Assert.Equal(ProductId, line.ProductId);
        Assert.Equal(500m, line.Quantity);
        Assert.Equal(UnitId, line.UnitId);
        Assert.Equal(LocationId, line.LocationId);
        Assert.Equal(expiry, line.ExpiryDate);
        Assert.Equal(2.99m, line.Price);
        Assert.Null(line.SkuId);
    }

    [Fact]
    public void Confirm_Fails_When_Line_Is_Dismissed()
    {
        var line = MakeLine();
        line.Dismiss();

        var result = line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyDismissed", result.Error.Code);
    }

    [Fact]
    public void Confirm_Fails_When_Line_Is_Already_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        line.MarkCommitted(Guid.NewGuid(), null);

        var result = line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyCommitted", result.Error.Code);
    }

    [Fact]
    public void ConfirmAsNew_Sets_New_Product_Intent_And_Leaves_ProductId_Null()
    {
        var line = MakeLine();
        var categoryId = Guid.CreateVersion7();

        var result = line.ConfirmAsNew("  Oat Milk  ", categoryId, 2m, UnitId, LocationId, null, 4.49m);

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Confirmed, line.Status);
        Assert.True(line.IsNewProduct);
        Assert.Null(line.ProductId);
        Assert.Equal("Oat Milk", line.NewProductName); // trimmed
        Assert.Equal(categoryId, line.NewProductCategoryId);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(UnitId, line.UnitId);
        Assert.Equal(LocationId, line.LocationId);
        Assert.Equal(4.49m, line.Price);
    }

    [Fact]
    public void ConfirmAsNew_Fails_When_Line_Is_Dismissed()
    {
        var line = MakeLine();
        line.Dismiss();

        var result = line.ConfirmAsNew("Oat Milk", Guid.CreateVersion7(), 1m, UnitId, LocationId, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyDismissed", result.Error.Code);
    }

    [Fact]
    public void ConfirmAsNew_Fails_When_Line_Is_Already_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        line.MarkCommitted(Guid.NewGuid(), null);

        var result = line.ConfirmAsNew("Oat Milk", Guid.CreateVersion7(), 1m, UnitId, LocationId, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyCommitted", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfirmAsNew_Fails_When_Name_Is_Blank(string blankName)
    {
        var line = MakeLine();

        var result = line.ConfirmAsNew(blankName, Guid.CreateVersion7(), 1m, UnitId, LocationId, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.MissingProductName", result.Error.Code);
        Assert.Equal(LineStatus.Pending, line.Status); // not transitioned
    }

    [Fact]
    public void Confirm_Clears_Prior_New_Product_Intent()
    {
        var line = MakeLine();
        line.ConfirmAsNew("Oat Milk", Guid.CreateVersion7(), 1m, UnitId, LocationId, null, null);

        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        Assert.False(line.IsNewProduct);
        Assert.Equal(ProductId, line.ProductId);
        Assert.Null(line.NewProductName);
        Assert.Null(line.NewProductCategoryId);
    }

    [Fact]
    public void Dismiss_Sets_Status_To_Dismissed()
    {
        var line = MakeLine();

        var result = line.Dismiss();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Dismissed, line.Status);
    }

    [Fact]
    public void Dismiss_Fails_When_Line_Is_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        line.MarkCommitted(Guid.NewGuid(), null);

        var result = line.Dismiss();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineAlreadyCommitted", result.Error.Code);
    }

    [Fact]
    public void Restore_Returns_Dismissed_Line_To_Pending()
    {
        var line = MakeLine();
        line.Dismiss();

        var result = line.Restore();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);
    }

    [Fact]
    public void Restore_Fails_When_Line_Is_Not_Dismissed()
    {
        var line = MakeLine(); // Pending

        var result = line.Restore();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotDismissed", result.Error.Code);
    }

    [Fact]
    public void Restore_Fails_When_Line_Is_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        line.MarkCommitted(Guid.NewGuid(), null);

        var result = line.Restore();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotDismissed", result.Error.Code);
        Assert.Equal(LineStatus.Committed, line.Status); // unchanged
    }

    // ── Reopen (plantry-v0wl) ──────────────────────────────────────────────────────

    [Fact]
    public void Reopen_Returns_A_Confirmed_Line_To_Pending()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, 2.50m);

        var result = line.Reopen();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);
    }

    [Fact]
    public void Reopen_Clears_User_Resolved_Fields_But_Preserves_Suggestion_And_Receipt_Data()
    {
        var sugId = Guid.CreateVersion7();
        var session = ImportSession.Start(Household, ImportSourceType.Receipt, Guid.CreateVersion7(), Clock);
        var line = session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: sugId, suggestedProductName: "Bananas", suggestedQuantity: 1.34m,
            suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);
        line.Confirm(ProductId, skuId: Guid.CreateVersion7(), 7m, UnitId, LocationId,
            new DateOnly(2027, 1, 1), 0.79m);

        var result = line.Reopen();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);

        // User-resolved fields are cleared so the prefill chain re-derives from the suggestions.
        Assert.Null(line.ProductId);
        Assert.Null(line.SkuId);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitId);
        Assert.Null(line.LocationId);
        Assert.Null(line.ExpiryDate);
        Assert.Null(line.Price);
        Assert.False(line.IsNewProduct);

        // Once-set receipt/suggestion data survives untouched — the prefill chain has its inputs back.
        Assert.Equal(sugId, line.SuggestedProductId);
        Assert.Equal("Bananas", line.SuggestedProductName);
        Assert.Equal(1.34m, line.SuggestedQuantity);
        Assert.Equal("lb", line.SuggestedUnitLabel);
        Assert.Equal(0.79m, line.SuggestedPrice);
        Assert.Equal(1.34m, line.ReceiptWeight);
        Assert.Equal("lb", line.ReceiptWeightUnitLabel);
        Assert.Equal(7m, line.EstimatedEachCount);
        Assert.Equal(SuggestedConfidence.High, line.EstimatedEachConfidence);
    }

    [Fact]
    public void Reopen_Clears_Prior_New_Product_Intent()
    {
        var line = MakeLine();
        line.ConfirmAsNew("Oat Milk", Guid.CreateVersion7(), 1m, UnitId, LocationId, null, 4.49m);

        var result = line.Reopen();

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Pending, line.Status);
        Assert.False(line.IsNewProduct);
        Assert.Null(line.NewProductName);
        Assert.Null(line.NewProductCategoryId);
    }

    [Fact]
    public void Reopened_Line_Can_Be_Resolved_Again()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, 2.50m);
        line.Reopen();

        var newProduct = Guid.CreateVersion7();
        var result = line.Confirm(newProduct, null, 3m, UnitId, LocationId, null, 9.99m);

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Confirmed, line.Status);
        Assert.Equal(newProduct, line.ProductId);
        Assert.Equal(3m, line.Quantity);
    }

    [Fact]
    public void Reopen_Fails_When_Line_Is_Pending()
    {
        var line = MakeLine(); // Pending, never confirmed

        var result = line.Reopen();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
        Assert.Equal(LineStatus.Pending, line.Status);
    }

    [Fact]
    public void Reopen_Fails_When_Line_Is_Dismissed()
    {
        var line = MakeLine();
        line.Dismiss();

        var result = line.Reopen();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
        Assert.Equal(LineStatus.Dismissed, line.Status);
    }

    [Fact]
    public void Reopen_Fails_When_Line_Is_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        line.MarkCommitted(Guid.NewGuid(), null);

        var result = line.Reopen();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
        Assert.Equal(LineStatus.Committed, line.Status); // unchanged
    }

    [Fact]
    public void MarkCommitted_Records_Linkage_And_Transitions_To_Committed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);
        var journalId = Guid.CreateVersion7();
        var priceObsId = Guid.CreateVersion7();
        var createdProductId = Guid.CreateVersion7();

        var result = line.MarkCommitted(journalId, priceObsId, createdProductId);

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Committed, line.Status);
        Assert.Equal(journalId, line.JournalId);
        Assert.Equal(priceObsId, line.PriceObservationId);
        Assert.Equal(createdProductId, line.CreatedProductId);
    }

    [Fact]
    public void MarkCommitted_With_Null_PriceObservationId_Is_Allowed()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        var result = line.MarkCommitted(Guid.NewGuid(), null);

        Assert.True(result.IsSuccess);
        Assert.Equal(LineStatus.Committed, line.Status);
        Assert.Null(line.PriceObservationId);
    }

    [Fact]
    public void MarkCommitted_Fails_When_Line_Is_Not_Confirmed()
    {
        var line = MakeLine(); // Pending

        var result = line.MarkCommitted(Guid.NewGuid(), null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
    }

    [Fact]
    public void MarkCommitted_Fails_When_Line_Is_Dismissed()
    {
        var line = MakeLine();
        line.Dismiss();

        var result = line.MarkCommitted(Guid.NewGuid(), null);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotConfirmed", result.Error.Code);
    }

    [Fact]
    public void MarkAmended_Records_Corrected_Quantity_And_Timestamp_On_A_Committed_Line()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, 3.98m);
        line.MarkCommitted(Guid.CreateVersion7(), Guid.CreateVersion7());
        var amendedAt = Clock.UtcNow;

        var result = line.MarkAmended(3m, amendedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(3m, line.AmendedQuantity);
        Assert.Equal(amendedAt, line.AmendedAt);
    }

    [Fact]
    public void MarkAmended_Second_Amendment_Overwrites_The_First_ADR_023_A3()
    {
        var line = MakeLine();
        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, 3.98m);
        line.MarkCommitted(Guid.CreateVersion7(), Guid.CreateVersion7());
        line.MarkAmended(3m, Clock.UtcNow);

        var secondAmendedAt = Clock.UtcNow;
        var result = line.MarkAmended(2.5m, secondAmendedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(2.5m, line.AmendedQuantity);
        Assert.Equal(secondAmendedAt, line.AmendedAt);
    }

    [Fact]
    public void MarkAmended_Fails_When_Line_Is_Not_Committed()
    {
        var line = MakeLine(); // Pending

        var result = line.MarkAmended(3m, Clock.UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.LineNotCommitted", result.Error.Code);
        Assert.Null(line.AmendedQuantity);
    }

    [Fact]
    public void RawParse_Is_Preserved_After_Confirm_And_Dismiss()
    {
        var line = MakeLine();
        var originalRaw = line.RawParse;

        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        Assert.Equal(originalRaw, line.RawParse);
    }

    [Fact]
    public void Create_Carries_Suggestion_Fields()
    {
        var sugId = Guid.CreateVersion7();
        var line = MakeLineWithSuggestions(
            suggestedProductId: sugId,
            suggestedProductName: "Oat Milk",
            suggestedQuantity: 2m,
            suggestedUnitLabel: "L",
            suggestedPrice: 3.49m);

        Assert.Equal(sugId, line.SuggestedProductId);
        Assert.Equal("Oat Milk", line.SuggestedProductName);
        Assert.Equal(2m, line.SuggestedQuantity);
        Assert.Equal("L", line.SuggestedUnitLabel);
        Assert.Equal(3.49m, line.SuggestedPrice);
    }

    [Fact]
    public void Confirm_Does_Not_Mutate_Suggestion_Fields()
    {
        var sugId = Guid.CreateVersion7();
        var line = MakeLineWithSuggestions(
            suggestedProductId: sugId,
            suggestedProductName: "Oat Milk",
            suggestedQuantity: 2m,
            suggestedUnitLabel: "L",
            suggestedPrice: 3.49m);

        line.Confirm(ProductId, null, 5m, UnitId, LocationId, null, 9.99m);

        Assert.Equal(sugId, line.SuggestedProductId);
        Assert.Equal("Oat Milk", line.SuggestedProductName);
        Assert.Equal(2m, line.SuggestedQuantity);
        Assert.Equal("L", line.SuggestedUnitLabel);
        Assert.Equal(3.49m, line.SuggestedPrice);
    }
}
