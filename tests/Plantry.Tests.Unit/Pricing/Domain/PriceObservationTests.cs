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

    // ── ADR-023 A7/A8: amendment supersede-append ───────────────────────────────

    [Fact]
    public void RecordAmendment_Copies_Price_ObservedAt_And_SourceRef_From_The_Original()
    {
        var storeId = Guid.CreateVersion7();
        var original = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.98m, quantity: 1m, unitId: UnitId,
            unitPrice: 3.98m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId,
            storeId: storeId);

        var amendedUserId = Guid.CreateVersion7();
        var amendment = PriceObservation.RecordAmendment(original, correctedQuantity: 3m, unitPrice: 1.3267m, amendedUserId);

        Assert.Equal(original.Price, amendment.Price);
        Assert.Equal(original.ObservedAt, amendment.ObservedAt);
        Assert.Equal(original.SourceRef, amendment.SourceRef);
        Assert.Equal(original.HouseholdId, amendment.HouseholdId);
        Assert.Equal(original.ProductId, amendment.ProductId);
        Assert.Equal(original.Source, amendment.Source);
        Assert.Equal(original.MerchantText, amendment.MerchantText);
        Assert.Equal(storeId, amendment.StoreId);
    }

    [Fact]
    public void RecordAmendment_Sets_Corrected_Quantity_And_Recomputed_UnitPrice()
    {
        var original = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.98m, quantity: 1m, unitId: UnitId,
            unitPrice: 3.98m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        var amendment = PriceObservation.RecordAmendment(original, correctedQuantity: 3m, unitPrice: 1.3267m, UserId);

        Assert.Equal(3m, amendment.Quantity);
        Assert.Equal(1.3267m, amendment.UnitPrice);
    }

    [Fact]
    public void RecordAmendment_Sets_AmendsId_To_The_Original_Id_And_Mints_A_New_Id()
    {
        var original = PriceObservation.Record(
            Household, ProductId, null, 3.98m, 1m, UnitId, 3.98m,
            PriceSource.Purchase, "Superstore", SourceRef, Now, UserId);

        var amendment = PriceObservation.RecordAmendment(original, 3m, 1.3267m, UserId);

        Assert.Equal(original.Id, amendment.AmendsId);
        Assert.NotEqual(original.Id, amendment.Id);
        Assert.Null(amendment.SupersededById); // a fresh amending row is itself never pre-superseded
        Assert.Null(original.AmendsId); // the original purchase row does not itself amend anything
    }

    [Fact]
    public void Supersede_Binds_SupersededById_When_Currently_Null()
    {
        var original = PriceObservation.Record(
            Household, ProductId, null, 3.98m, 1m, UnitId, 3.98m,
            PriceSource.Purchase, "Superstore", SourceRef, Now, UserId);
        var replacementId = PriceObservationId.New();

        original.Supersede(replacementId);

        Assert.Equal(replacementId, original.SupersededById);
    }

    [Fact]
    public void Supersede_Throws_When_Already_Superseded_So_A_Repeat_Cannot_Fork_Off_A_Dead_Row()
    {
        var original = PriceObservation.Record(
            Household, ProductId, null, 3.98m, 1m, UnitId, 3.98m,
            PriceSource.Purchase, "Superstore", SourceRef, Now, UserId);
        var firstReplacementId = PriceObservationId.New();
        original.Supersede(firstReplacementId);

        var secondReplacementId = PriceObservationId.New();
        var ex = Assert.Throws<InvalidOperationException>(() => original.Supersede(secondReplacementId));

        Assert.Contains(firstReplacementId.ToString(), ex.Message);
        Assert.Equal(firstReplacementId, original.SupersededById); // unchanged — the second call never overwrites
    }

    [Fact]
    public void Supersede_Leaves_The_Immutable_Price_Event_Untouched()
    {
        var original = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.98m, quantity: 1m, unitId: UnitId,
            unitPrice: 3.98m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        original.Supersede(PriceObservationId.New());

        Assert.Equal(3.98m, original.Price);
        Assert.Equal(1m, original.Quantity);
        Assert.Equal(3.98m, original.UnitPrice);
        Assert.Equal(PriceSource.Purchase, original.Source);
        Assert.Equal(SourceRef, original.SourceRef);
        Assert.Equal(Now, original.ObservedAt);
    }

    [Fact]
    public void Worked_Example_Two_Successive_Amendments_Chain_Off_The_Live_Row()
    {
        // The 2026-07-20 incident (purchase-entry-amendment.md §3): onions entered 1 lb, actually 3 lb,
        // then corrected again to 2.5 lb.
        var purchase = PriceObservation.Record(
            Household, ProductId, null,
            price: 3.98m, quantity: 1m, unitId: UnitId,
            unitPrice: 3.98m, source: PriceSource.Purchase,
            merchantText: "Superstore", sourceRef: SourceRef, observedAt: Now, userId: UserId);

        var firstAmendment = PriceObservation.RecordAmendment(purchase, correctedQuantity: 3m, unitPrice: 1.33m, UserId);
        purchase.Supersede(firstAmendment.Id);

        var secondAmendment = PriceObservation.RecordAmendment(firstAmendment, correctedQuantity: 2.5m, unitPrice: 1.59m, UserId);
        firstAmendment.Supersede(secondAmendment.Id);

        Assert.Equal(firstAmendment.Id, purchase.SupersededById);
        Assert.Equal(secondAmendment.Id, firstAmendment.SupersededById);
        Assert.Null(secondAmendment.SupersededById); // the live row — still un-superseded

        Assert.Equal(purchase.Id, firstAmendment.AmendsId);
        Assert.Equal(firstAmendment.Id, secondAmendment.AmendsId);

        // Same Price/ObservedAt/SourceRef preserved across the whole chain.
        Assert.Equal(3.98m, secondAmendment.Price);
        Assert.Equal(Now, secondAmendment.ObservedAt);
        Assert.Equal(SourceRef, secondAmendment.SourceRef);
        Assert.Equal(2.5m, secondAmendment.Quantity);
        Assert.Equal(1.59m, secondAmendment.UnitPrice);

        // Repeating off the already-superseded purchase row (never the live one) is rejected.
        Assert.Throws<InvalidOperationException>(() => purchase.Supersede(PriceObservationId.New()));
    }
}
