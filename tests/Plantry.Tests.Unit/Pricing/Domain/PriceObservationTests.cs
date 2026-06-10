using Plantry.Pricing.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Pricing.Domain;

public sealed class PriceObservationTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Record_Sets_All_Fields()
    {
        var skuId = Guid.CreateVersion7();

        var obs = PriceObservation.Record(
            Household, ProductId, skuId,
            price: 3.99m, quantity: 500m, unitId: UnitId,
            unitPrice: 0.00798m,
            source: PriceSource.Purchase,
            merchantText: "Superstore",
            sourceRef: SourceRef,
            observedAt: Now,
            userId: UserId);

        Assert.Equal(Household, obs.HouseholdId);
        Assert.Equal(ProductId, obs.ProductId);
        Assert.Equal(skuId, obs.SkuId);
        Assert.Equal(3.99m, obs.Price);
        Assert.Equal(500m, obs.Quantity);
        Assert.Equal(UnitId, obs.UnitId);
        Assert.Equal(0.00798m, obs.UnitPrice);
        Assert.Equal(PriceSource.Purchase, obs.Source);
        Assert.Equal("Superstore", obs.MerchantText);
        Assert.Equal(SourceRef, obs.SourceRef);
        Assert.Equal(Now, obs.ObservedAt);
        Assert.Equal(UserId, obs.UserId);
        Assert.NotEqual(Guid.Empty, obs.Id.Value);
    }

    [Fact]
    public void Record_Stores_Null_UnitPrice_When_Calculator_Returned_Null()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 2m, quantity: 1m, unitId: UnitId,
            unitPrice: null,
            source: PriceSource.Purchase,
            merchantText: null,
            sourceRef: SourceRef,
            observedAt: Now,
            userId: UserId);

        Assert.Null(obs.UnitPrice);
    }

    [Fact]
    public void Record_Stores_Value_UnitPrice_When_Calculator_Returned_Value()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 2m, quantity: 1000m, unitId: UnitId,
            unitPrice: 0.002m,
            source: PriceSource.Purchase,
            merchantText: null,
            sourceRef: SourceRef,
            observedAt: Now,
            userId: UserId);

        Assert.Equal(0.002m, obs.UnitPrice);
    }

    [Fact]
    public void Record_Each_Call_Produces_A_Distinct_Id()
    {
        var obs1 = PriceObservation.Record(Household, ProductId, null, 1m, 1m, UnitId, null,
            PriceSource.Purchase, null, SourceRef, Now, UserId);
        var obs2 = PriceObservation.Record(Household, ProductId, null, 1m, 1m, UnitId, null,
            PriceSource.Purchase, null, SourceRef, Now, UserId);

        Assert.NotEqual(obs1.Id, obs2.Id);
    }

    [Fact]
    public void Record_SkuId_Is_Null_When_Not_Provided()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 1m, quantity: 1m, unitId: UnitId,
            unitPrice: 1m, source: PriceSource.Deal,
            merchantText: null, sourceRef: SourceRef, observedAt: Now, userId: UserId);

        Assert.Null(obs.SkuId);
    }
}
