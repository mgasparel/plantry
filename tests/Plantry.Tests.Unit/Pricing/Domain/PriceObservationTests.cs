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

    [Fact]
    public void Record_Deal_Carries_Validity_Window_And_StoreId()
    {
        var storeId = Guid.CreateVersion7();
        var from = new DateOnly(2026, 7, 1);
        var to = new DateOnly(2026, 7, 7);

        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 2.50m, quantity: 1m, unitId: UnitId,
            unitPrice: 2.50m, source: PriceSource.Deal,
            merchantText: "Flyer", sourceRef: SourceRef, observedAt: Now, userId: UserId,
            validFrom: from, validTo: to, storeId: storeId);

        Assert.Equal(PriceSource.Deal, obs.Source);
        Assert.Equal(from, obs.ValidFrom);
        Assert.Equal(to, obs.ValidTo);
        Assert.Equal(storeId, obs.StoreId);
    }

    [Fact]
    public void Record_Purchase_Leaves_Window_And_StoreId_Null()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.99m, quantity: 500m, unitId: UnitId,
            unitPrice: 0.00798m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        Assert.Null(obs.ValidFrom);
        Assert.Null(obs.ValidTo);
        Assert.Null(obs.StoreId);
    }

    [Fact]
    public void ResolveStore_Binds_StoreId_When_Currently_Null()
    {
        var storeId = Guid.CreateVersion7();
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.99m, quantity: 500m, unitId: UnitId,
            unitPrice: 0.00798m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        var bound = obs.ResolveStore(storeId);

        Assert.True(bound);
        Assert.Equal(storeId, obs.StoreId);
    }

    [Fact]
    public void ResolveStore_Is_A_NoOp_When_StoreId_Already_Set()
    {
        var original = Guid.CreateVersion7();
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 2.50m, quantity: 1m, unitId: UnitId,
            unitPrice: 2.50m, source: PriceSource.Deal,
            merchantText: "Flyer", sourceRef: SourceRef, observedAt: Now, userId: UserId,
            validFrom: new DateOnly(2026, 7, 1), validTo: new DateOnly(2026, 7, 7), storeId: original);

        var bound = obs.ResolveStore(Guid.CreateVersion7());

        Assert.False(bound);
        Assert.Equal(original, obs.StoreId); // unchanged — the second call never overwrites
    }

    [Fact]
    public void Record_Manual_Source_Allows_Null_SourceRef()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.99m, quantity: 500m, unitId: UnitId,
            unitPrice: 0.00798m, source: PriceSource.Manual,
            merchantText: null, sourceRef: null, observedAt: Now, userId: UserId);

        Assert.Equal(PriceSource.Manual, obs.Source);
        Assert.Null(obs.SourceRef);
        Assert.Null(obs.MerchantText);
        Assert.Null(obs.StoreId);
    }

    [Fact]
    public void ResolveStore_Leaves_The_Immutable_Price_Event_Untouched()
    {
        var obs = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.99m, quantity: 500m, unitId: UnitId,
            unitPrice: 0.00798m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        obs.ResolveStore(Guid.CreateVersion7());

        Assert.Equal(3.99m, obs.Price);
        Assert.Equal(500m, obs.Quantity);
        Assert.Equal(UnitId, obs.UnitId);
        Assert.Equal(0.00798m, obs.UnitPrice);
        Assert.Equal(PriceSource.Purchase, obs.Source);
        Assert.Equal("Superstore", obs.MerchantText);
        Assert.Equal(SourceRef, obs.SourceRef);
        Assert.Equal(Now, obs.ObservedAt);
        Assert.Equal(UserId, obs.UserId);
    }
}
