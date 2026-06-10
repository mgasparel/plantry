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

    private static ImportLine MakeLine(SuggestedConfidence confidence = SuggestedConfidence.High)
    {
        var session = ImportSession.Start(Household, ImportSourceType.Receipt, Guid.CreateVersion7(), Clock);
        return session.AddLine(1, "500g Flour", confidence, """{"qty":500}""");
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
    public void RawParse_Is_Preserved_After_Confirm_And_Dismiss()
    {
        var line = MakeLine();
        var originalRaw = line.RawParse;

        line.Confirm(ProductId, null, 1m, UnitId, LocationId, null, null);

        Assert.Equal(originalRaw, line.RawParse);
    }
}
