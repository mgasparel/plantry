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
