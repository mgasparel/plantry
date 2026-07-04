using Plantry.Catalog.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Catalog.Domain;

/// <summary>
/// L1 unit tests for the <see cref="Product"/> aggregate — the heavily-tested core of Slice 1
/// Stage B (PHASE-1-PLAN.md "near-exhaustively unit-tested"). Covers creation, mutation,
/// SKU/conversion child management, the parent/variant depth-1 invariant, and archival.
/// </summary>
public sealed class ProductTests
{
    private static readonly HouseholdId HouseholdId = HouseholdId.New();
    private static readonly UnitId UnitId = Plantry.Catalog.Domain.UnitId.New();
    private static readonly IClock Clock = SystemClock.Instance;

    private static Product NewProduct(string name = "Flour") =>
        Product.Create(HouseholdId, name, UnitId, Clock);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Sets_Properties_And_Trims_Name()
    {
        var product = Product.Create(HouseholdId, "  Oat Milk  ", UnitId, Clock);

        Assert.Equal("Oat Milk", product.Name);
        Assert.Equal(HouseholdId, product.HouseholdId);
        Assert.Equal(UnitId, product.DefaultUnitId);
        Assert.Null(product.ParentProductId);
        Assert.Null(product.CategoryId);
        Assert.Null(product.DefaultLocationId);
        Assert.False(product.HasVariants);
        Assert.False(product.IsArchived);
        Assert.True(product.TrackStock); // ordinary goods track stock by default
        Assert.Empty(product.Skus);
        Assert.Empty(product.Conversions);
    }

    [Fact]
    public void Create_Can_Mint_An_Untracked_Staple()
    {
        var staple = Product.Create(HouseholdId, "Salt", UnitId, Clock, trackStock: false);

        Assert.False(staple.TrackStock);
    }

    [Fact]
    public void SetTrackStock_Toggles_The_Flag()
    {
        var staple = Product.Create(HouseholdId, "Salt", UnitId, Clock, trackStock: false);

        staple.SetTrackStock(true, Clock);

        Assert.True(staple.TrackStock);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Rejects_Blank_Name(string name)
    {
        Assert.Throws<ArgumentException>(() => Product.Create(HouseholdId, name, UnitId, Clock));
    }

    // ── Derived flags ─────────────────────────────────────────────────────────

    [Fact]
    public void NewProduct_IsNeitherParentNorVariant_AndCanHoldStock()
    {
        var product = NewProduct();

        Assert.False(product.IsParent);
        Assert.False(product.IsVariant);
        Assert.True(product.CanHoldStock);
    }

    [Fact]
    public void IsVariant_True_Once_ParentProductId_Set()
    {
        var product = NewProduct();
        var parentId = ProductId.New();

        product.MakeVariantOf(parentId, Clock);

        Assert.True(product.IsVariant);
        Assert.Equal(parentId, product.ParentProductId);
    }

    [Fact]
    public void IsParent_And_CannotHoldStock_Once_HasVariants_Is_Set()
    {
        var product = NewProduct();

        product.SetHasVariants(true, Clock);

        Assert.True(product.IsParent);
        Assert.False(product.CanHoldStock);
    }

    // ── Rename / simple setters ──────────────────────────────────────────────

    [Fact]
    public void Rename_Trims_And_Updates_Name()
    {
        var product = NewProduct();

        product.Rename("  Oat Milk (renamed)  ", Clock);

        Assert.Equal("Oat Milk (renamed)", product.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_Rejects_Blank_Name(string name)
    {
        var product = NewProduct();

        Assert.Throws<ArgumentException>(() => product.Rename(name, Clock));
    }

    [Fact]
    public void SetCategory_Stores_Value_And_Allows_Clearing()
    {
        var product = NewProduct();
        var categoryId = CategoryId.New();

        product.SetCategory(categoryId, Clock);
        Assert.Equal(categoryId, product.CategoryId);

        product.SetCategory(null, Clock);
        Assert.Null(product.CategoryId);
    }

    [Fact]
    public void SetDefaultUnit_Stores_Value()
    {
        var product = NewProduct();
        var newUnitId = Plantry.Catalog.Domain.UnitId.New();

        product.SetDefaultUnit(newUnitId, Clock);

        Assert.Equal(newUnitId, product.DefaultUnitId);
    }

    [Fact]
    public void SetDefaultLocation_Stores_Value_And_Allows_Clearing()
    {
        var product = NewProduct();
        var locationId = LocationId.New();

        product.SetDefaultLocation(locationId, Clock);
        Assert.Equal(locationId, product.DefaultLocationId);

        product.SetDefaultLocation(null, Clock);
        Assert.Null(product.DefaultLocationId);
    }

    // ── Expiry defaults ──────────────────────────────────────────────────────

    [Fact]
    public void SetExpiryDefaults_Stores_All_Four_Values()
    {
        var product = NewProduct();

        product.SetExpiryDefaults(7, 3, 90, 2, Clock);

        Assert.Equal(7, product.DefaultDueDays);
        Assert.Equal(3, product.DefaultDueDaysAfterOpening);
        Assert.Equal(90, product.DefaultDueDaysAfterFreezing);
        Assert.Equal(2, product.DefaultDueDaysAfterThawing);
    }

    [Fact]
    public void SetExpiryDefaults_Allows_All_Null()
    {
        var product = NewProduct();
        product.SetExpiryDefaults(7, 3, 90, 2, Clock);

        product.SetExpiryDefaults(null, null, null, null, Clock);

        Assert.Null(product.DefaultDueDays);
        Assert.Null(product.DefaultDueDaysAfterOpening);
        Assert.Null(product.DefaultDueDaysAfterFreezing);
        Assert.Null(product.DefaultDueDaysAfterThawing);
    }

    [Theory]
    [InlineData(-1, null, null, null)]
    [InlineData(null, -1, null, null)]
    [InlineData(null, null, -1, null)]
    [InlineData(null, null, null, -1)]
    public void SetExpiryDefaults_Rejects_Negative_In_Any_Slot(int? dueDays, int? afterOpening, int? afterFreezing, int? afterThawing)
    {
        var product = NewProduct();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            product.SetExpiryDefaults(dueDays, afterOpening, afterFreezing, afterThawing, Clock));
    }

    // ── InheritFrom (parent → variant handshake) ─────────────────────────────

    [Fact]
    public void InheritFrom_Copies_Unset_Expiry_Defaults_From_Parent()
    {
        var parent = NewProduct("Parent");
        parent.SetExpiryDefaults(7, 3, 90, 2, Clock);
        var variant = NewProduct("Variant");

        variant.InheritFrom(parent, Clock);

        Assert.Equal(7, variant.DefaultDueDays);
        Assert.Equal(3, variant.DefaultDueDaysAfterOpening);
        Assert.Equal(90, variant.DefaultDueDaysAfterFreezing);
        Assert.Equal(2, variant.DefaultDueDaysAfterThawing);
    }

    [Fact]
    public void InheritFrom_Leaves_Already_Set_Expiry_Defaults_Untouched()
    {
        var parent = NewProduct("Parent");
        parent.SetExpiryDefaults(7, 3, 90, 2, Clock);
        var variant = NewProduct("Variant");
        variant.SetExpiryDefaults(1, null, null, null, Clock);

        variant.InheritFrom(parent, Clock);

        Assert.Equal(1, variant.DefaultDueDays);
        Assert.Equal(3, variant.DefaultDueDaysAfterOpening);
        Assert.Equal(90, variant.DefaultDueDaysAfterFreezing);
        Assert.Equal(2, variant.DefaultDueDaysAfterThawing);
    }

    [Fact]
    public void InheritFrom_Copies_Parent_Conversions_When_Variant_Has_None()
    {
        var parent = NewProduct("Parent");
        var fromUnit = Plantry.Catalog.Domain.UnitId.New();
        var toUnit = Plantry.Catalog.Domain.UnitId.New();
        parent.AddConversion(fromUnit, toUnit, 120m, Clock);
        var variant = NewProduct("Variant");

        variant.InheritFrom(parent, Clock);

        var conversion = Assert.Single(variant.Conversions);
        Assert.Equal(fromUnit, conversion.FromUnitId);
        Assert.Equal(toUnit, conversion.ToUnitId);
        Assert.Equal(120m, conversion.Factor);
        Assert.Equal(variant.Id, conversion.ProductId);
        Assert.Equal(variant.HouseholdId, conversion.HouseholdId);
    }

    [Fact]
    public void InheritFrom_Does_Not_Copy_Conversions_When_Variant_Already_Has_Its_Own()
    {
        var parent = NewProduct("Parent");
        parent.AddConversion(Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 120m, Clock);
        var variant = NewProduct("Variant");
        var ownConversion = variant.AddConversion(Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 4m, Clock);

        variant.InheritFrom(parent, Clock);

        Assert.Same(ownConversion, Assert.Single(variant.Conversions));
    }

    [Fact]
    public void InheritFrom_Is_A_NoOp_When_Parent_Has_Nothing_To_Inherit()
    {
        var parent = NewProduct("Parent");
        var variant = NewProduct("Variant");

        variant.InheritFrom(parent, Clock);

        Assert.Null(variant.DefaultDueDays);
        Assert.Null(variant.DefaultDueDaysAfterOpening);
        Assert.Null(variant.DefaultDueDaysAfterFreezing);
        Assert.Null(variant.DefaultDueDaysAfterThawing);
        Assert.Empty(variant.Conversions);
    }

    // ── SKUs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AddSku_Appends_To_Skus_And_Returns_It()
    {
        var product = NewProduct();

        var sku = product.AddSku("1 kg bag", 1000m, UnitId, Clock);

        Assert.Same(sku, Assert.Single(product.Skus));
        Assert.Equal("1 kg bag", sku.Label);
        Assert.Equal(product.Id, sku.ProductId);
        Assert.Equal(product.HouseholdId, sku.HouseholdId);
    }

    [Fact]
    public void AddSku_Allows_Null_Size()
    {
        var product = NewProduct();

        var sku = product.AddSku("Loose", null, null, Clock);

        Assert.Null(sku.SizeQuantity);
        Assert.Null(sku.SizeUnitId);
    }

    [Fact]
    public void RemoveSku_Removes_Matching_Child()
    {
        var product = NewProduct();
        var sku = product.AddSku("1 kg bag", 1000m, UnitId, Clock);

        product.RemoveSku(sku.Id, Clock);

        Assert.Empty(product.Skus);
    }

    [Fact]
    public void RemoveSku_Throws_When_Sku_Does_Not_Belong_To_Product()
    {
        var product = NewProduct();

        Assert.Throws<InvalidOperationException>(() => product.RemoveSku(ProductSkuId.New(), Clock));
    }

    // ── Conversions ──────────────────────────────────────────────────────────

    [Fact]
    public void AddConversion_Appends_To_Conversions_And_Returns_It()
    {
        var product = NewProduct();
        var fromUnit = Plantry.Catalog.Domain.UnitId.New();
        var toUnit = Plantry.Catalog.Domain.UnitId.New();

        var conversion = product.AddConversion(fromUnit, toUnit, 120m, Clock);

        Assert.Same(conversion, Assert.Single(product.Conversions));
        Assert.Equal(fromUnit, conversion.FromUnitId);
        Assert.Equal(toUnit, conversion.ToUnitId);
        Assert.Equal(120m, conversion.Factor);
        Assert.Equal(product.Id, conversion.ProductId);
        Assert.Equal(product.HouseholdId, conversion.HouseholdId);
    }

    // ── Provenance: AI-suggested vs user-confirmed (ADR-022) ─────────────────

    [Fact]
    public void AddConversion_Defaults_To_UserConfirmed()
    {
        var product = NewProduct();

        var conversion = product.AddConversion(
            Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 120m, Clock);

        Assert.Equal(ConversionSource.UserConfirmed, conversion.Source);
        Assert.False(conversion.IsAiSuggested);
    }

    [Fact]
    public void AddConversion_Records_AiSuggested_Provenance_When_Requested()
    {
        var product = NewProduct();

        var conversion = product.AddConversion(
            Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 5m, Clock,
            ConversionSource.AiSuggested);

        Assert.Equal(ConversionSource.AiSuggested, conversion.Source);
        Assert.True(conversion.IsAiSuggested);
    }

    [Fact]
    public void PromoteConversion_Flips_AiSuggested_To_UserConfirmed()
    {
        var product = NewProduct();
        var conversion = product.AddConversion(
            Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 5m, Clock,
            ConversionSource.AiSuggested);

        product.PromoteConversion(conversion.Id, Clock);

        Assert.Equal(ConversionSource.UserConfirmed, conversion.Source);
        Assert.False(conversion.IsAiSuggested);
    }

    [Fact]
    public void PromoteConversion_Is_Idempotent_On_An_Already_Confirmed_Conversion()
    {
        var product = NewProduct();
        var conversion = product.AddConversion(
            Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 120m, Clock);

        // Confirmed already → no-op success, no throw.
        product.PromoteConversion(conversion.Id, Clock);

        Assert.Equal(ConversionSource.UserConfirmed, conversion.Source);
    }

    [Fact]
    public void PromoteConversion_Throws_When_Conversion_Does_Not_Belong_To_Product()
    {
        var product = NewProduct();

        Assert.Throws<InvalidOperationException>(() => product.PromoteConversion(ProductConversionId.New(), Clock));
    }

    [Fact]
    public void AddConversion_UserConfirmed_Supersedes_An_Existing_Suggested_For_The_Same_Pair()
    {
        var product = NewProduct();
        var from = Plantry.Catalog.Domain.UnitId.New();
        var to = Plantry.Catalog.Domain.UnitId.New();
        product.AddConversion(from, to, 5m, Clock, ConversionSource.AiSuggested);

        var confirmed = product.AddConversion(from, to, 6m, Clock, ConversionSource.UserConfirmed);

        // The stale suggestion is dropped; only the confirmed factor remains for that pair.
        var remaining = Assert.Single(product.Conversions);
        Assert.Same(confirmed, remaining);
        Assert.Equal(6m, remaining.Factor);
        Assert.Equal(ConversionSource.UserConfirmed, remaining.Source);
    }

    [Fact]
    public void AddConversion_Suggested_Does_Not_Duplicate_Or_Overwrite_An_Existing_Conversion()
    {
        var product = NewProduct();
        var from = Plantry.Catalog.Domain.UnitId.New();
        var to = Plantry.Catalog.Domain.UnitId.New();
        var confirmed = product.AddConversion(from, to, 6m, Clock, ConversionSource.UserConfirmed);

        var returned = product.AddConversion(from, to, 5m, Clock, ConversionSource.AiSuggested);

        // The suggestion neither piles on nor overwrites — the existing entry wins and is returned.
        var remaining = Assert.Single(product.Conversions);
        Assert.Same(confirmed, remaining);
        Assert.Same(confirmed, returned);
        Assert.Equal(6m, remaining.Factor);
        Assert.Equal(ConversionSource.UserConfirmed, remaining.Source);
    }

    [Fact]
    public void InheritFrom_Preserves_Conversion_Provenance()
    {
        var parent = NewProduct("Bananas");
        var variant = NewProduct("Bananas (organic)");
        parent.AddConversion(
            Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 5m, Clock,
            ConversionSource.AiSuggested);

        variant.InheritFrom(parent, Clock);

        var inherited = Assert.Single(variant.Conversions);
        Assert.Equal(ConversionSource.AiSuggested, inherited.Source);
    }

    [Fact]
    public void RemoveConversion_Removes_Matching_Child()
    {
        var product = NewProduct();
        var conversion = product.AddConversion(Plantry.Catalog.Domain.UnitId.New(), Plantry.Catalog.Domain.UnitId.New(), 120m, Clock);

        product.RemoveConversion(conversion.Id, Clock);

        Assert.Empty(product.Conversions);
    }

    [Fact]
    public void RemoveConversion_Throws_When_Conversion_Does_Not_Belong_To_Product()
    {
        var product = NewProduct();

        Assert.Throws<InvalidOperationException>(() => product.RemoveConversion(ProductConversionId.New(), Clock));
    }

    // ── Parent/variant depth-1 invariant ─────────────────────────────────────

    [Fact]
    public void MakeVariantOf_Sets_ParentProductId()
    {
        var product = NewProduct();
        var parentId = ProductId.New();

        product.MakeVariantOf(parentId, Clock);

        Assert.Equal(parentId, product.ParentProductId);
    }

    [Fact]
    public void MakeVariantOf_Rejects_Self_As_Parent()
    {
        var product = NewProduct();

        var ex = Assert.Throws<ArgumentException>(() => product.MakeVariantOf(product.Id, Clock));
        Assert.Equal("parentId", ex.ParamName);
    }

    [Fact]
    public void MakeVariantOf_Rejects_When_Product_Already_Has_Variants()
    {
        var product = NewProduct();
        product.SetHasVariants(true, Clock);

        Assert.Throws<InvalidOperationException>(() => product.MakeVariantOf(ProductId.New(), Clock));
    }

    [Fact]
    public void DetachFromParent_Clears_ParentProductId()
    {
        var product = NewProduct();
        product.MakeVariantOf(ProductId.New(), Clock);

        product.DetachFromParent(Clock);

        Assert.Null(product.ParentProductId);
        Assert.False(product.IsVariant);
    }

    [Fact]
    public void SetHasVariants_Toggles_Flag_Both_Ways()
    {
        var product = NewProduct();

        product.SetHasVariants(true, Clock);
        Assert.True(product.HasVariants);

        product.SetHasVariants(false, Clock);
        Assert.False(product.HasVariants);
    }

    // ── Archive / unarchive ──────────────────────────────────────────────────

    [Fact]
    public void Archive_Sets_ArchivedAt_And_IsArchived()
    {
        var product = NewProduct();

        product.Archive(Clock);

        Assert.True(product.IsArchived);
        Assert.NotNull(product.ArchivedAt);
    }

    [Fact]
    public void Archive_Is_Idempotent()
    {
        var product = NewProduct();
        product.Archive(Clock);
        var firstArchivedAt = product.ArchivedAt;

        product.Archive(Clock);

        Assert.Equal(firstArchivedAt, product.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Clears_ArchivedAt()
    {
        var product = NewProduct();
        product.Archive(Clock);

        product.Unarchive(Clock);

        Assert.False(product.IsArchived);
        Assert.Null(product.ArchivedAt);
    }

    [Fact]
    public void Unarchive_Is_Idempotent_When_Not_Archived()
    {
        var product = NewProduct();

        product.Unarchive(Clock);

        Assert.False(product.IsArchived);
        Assert.Null(product.ArchivedAt);
    }
}
