using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.Web.Pages.Intake;
using Xunit;

namespace Plantry.Tests.Web.Intake;

/// <summary>
/// L4 unit tests for <see cref="IntakeReviewHydrationBuilder.Build"/> (plantry-uk4u) — the pure projection of a
/// loaded <see cref="SessionReviewView"/> into the island's <see cref="SessionHydration"/> payload, lifted out
/// of <c>ReviewModel</c>. These pin the non-trivial projection rules directly, without spinning up the page or
/// the HTTP pipeline: the alternatives gate (resolved-catalog-only, applied twice around the
/// <see cref="ImportLine.MinAlternativesForSuggestion"/> threshold), the weight→each estimate presence rules,
/// the merchant-text "Receipt" fallback, and the server-side prefill wiring. The byte-for-byte wire contract is
/// pinned separately by <c>ReviewHydrationContractTests</c>.
/// </summary>
public sealed class IntakeReviewHydrationBuilderTests
{
    private static readonly IntakeReviewHydrationBuilder Builder = new();

    private static readonly DateOnly Today = new(2026, 6, 15);
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid MilkId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CheddarSharpId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CheddarMarbleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UnmappedId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid LitreUnitId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid EachUnitId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid FridgeLocationId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly ReviewHandlerUrls Urls = new(
        Commit: "/c", Discard: "/d", SaveLine: "/s", DismissLine: "/di",
        RestoreLine: "/r", Reopen: "/ro", ConfirmLines: "/cl");

    private static ReviewReferenceData Reference() => new(
        Products:
        [
            new ReviewProductOption(MilkId, "Milk", "L", DefaultUnitId: LitreUnitId,
                DefaultLocationId: FridgeLocationId, Skus: [], DefaultDueDays: 7),
            new ReviewProductOption(CheddarSharpId, "Cheddar, Sharp", "ea", DefaultUnitId: EachUnitId,
                DefaultLocationId: FridgeLocationId, Skus: [], DefaultDueDays: 30),
            new ReviewProductOption(CheddarMarbleId, "Cheddar, Marble", "ea", DefaultUnitId: EachUnitId,
                DefaultLocationId: FridgeLocationId, Skus: [], DefaultDueDays: 30),
        ],
        Units:
        [
            new ReviewUnitOption(EachUnitId, "ea", "Each", ReviewUnitDimension.Count),
            new ReviewUnitOption(LitreUnitId, "L", "Litre", ReviewUnitDimension.Volume),
        ],
        Locations: [new ReviewLocationOption(FridgeLocationId, "Fridge")],
        Categories: [new ReviewCategoryOption(Guid.NewGuid(), "Dairy", 200)]);

    private static ReviewLineView Line(
        LineStatus status = LineStatus.Pending,
        Guid? suggestedProductId = null,
        string? suggestedProductName = null,
        decimal? suggestedQuantity = null,
        string? suggestedUnitLabel = null,
        decimal? suggestedPrice = null,
        IReadOnlyList<ReviewAlternativeView>? alternatives = null,
        decimal? receiptWeight = null,
        string? receiptWeightUnitLabel = null,
        decimal? estimatedEachCount = null,
        SuggestedConfidence? estimatedEachConfidence = null) =>
        new(
            LineId: Guid.NewGuid(),
            LineNo: 1,
            ReceiptText: "SOME ITEM",
            SuggestedConfidence: SuggestedConfidence.High,
            Status: status,
            ProductId: null,
            SkuId: null,
            Quantity: null,
            UnitId: null,
            LocationId: null,
            ExpiryDate: null,
            Price: null,
            IsNewProduct: false,
            NewProductName: null,
            NewProductCategoryId: null,
            SuggestedProductId: suggestedProductId,
            SuggestedProductName: suggestedProductName,
            SuggestedQuantity: suggestedQuantity,
            SuggestedUnitLabel: suggestedUnitLabel,
            SuggestedPrice: suggestedPrice,
            SuggestedAlternatives: alternatives,
            ReceiptWeight: receiptWeight,
            ReceiptWeightUnitLabel: receiptWeightUnitLabel,
            EstimatedEachCount: estimatedEachCount,
            EstimatedEachConfidence: estimatedEachConfidence);

    private static SessionReviewView Session(
        IReadOnlyList<ReviewLineView> lines,
        string? merchantText = "Test Grocer",
        ImportSourceType sourceType = ImportSourceType.Receipt) =>
        new(
            SessionId: Guid.NewGuid(),
            Status: ImportStatus.Ready,
            MerchantText: merchantText,
            ParseError: null,
            CreatedAt: Now,
            Lines: lines,
            ReferenceData: Reference(),
            SourceType: sourceType);

    private static SessionHydration Build(SessionReviewView session) =>
        Builder.Build(session, Today, Now, Urls);

    // ── Alternatives gate ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Fewer than 2 raw alternatives → no suggestion block (pre-resolution threshold)")]
    public void Alternatives_Below_Threshold_PreResolution_Are_Dropped()
    {
        // One raw alternative, catalog-resolvable — still below MinAlternativesForSuggestion (2).
        var line = Line(alternatives: [new ReviewAlternativeView(CheddarSharpId, "Cheddar, Sharp", 0.72m)]);

        var hydration = Build(Session([line]));

        Assert.Null(hydration.Lines[0].Alternatives);
    }

    [Fact(DisplayName = "2 raw alternatives but only 1 resolves to catalog → dropped (post-resolution threshold)")]
    public void Alternatives_Below_Threshold_After_Resolution_Are_Dropped()
    {
        // Two raw candidates but one has no catalog product id and one points at an unmapped id:
        // only the mapped one survives resolution, leaving a single entry < 2 → whole block dropped.
        var line = Line(alternatives:
        [
            new ReviewAlternativeView(CheddarSharpId, "Cheddar, Sharp", 0.72m),
            new ReviewAlternativeView(UnmappedId, "Ghost Cheese", 0.51m),
        ]);

        var hydration = Build(Session([line]));

        Assert.Null(hydration.Lines[0].Alternatives);
    }

    [Fact(DisplayName = "Null-product-id alternatives are filtered before the post-resolution count")]
    public void Alternatives_With_Null_ProductId_Are_Filtered()
    {
        var line = Line(alternatives:
        [
            new ReviewAlternativeView(CheddarSharpId, "Cheddar, Sharp", 0.72m),
            new ReviewAlternativeView(ProductId: null, "Unresolved Parser Name", 0.60m),
        ]);

        var hydration = Build(Session([line]));

        // Only one candidate resolves → below the 2-required floor → dropped.
        Assert.Null(hydration.Lines[0].Alternatives);
    }

    [Fact(DisplayName = "2+ catalog-resolved alternatives surface, named from the catalog, not the stored name")]
    public void Alternatives_Resolved_Surface_With_Catalog_Names()
    {
        var line = Line(alternatives:
        [
            // Stored name deliberately stale; the hydration must use the catalog's authoritative name.
            new ReviewAlternativeView(CheddarSharpId, "STALE SHARP NAME", 0.72m),
            new ReviewAlternativeView(CheddarMarbleId, "Cheddar, Marble", 0.53m),
        ]);

        var hydration = Build(Session([line]));

        var alts = hydration.Lines[0].Alternatives;
        Assert.NotNull(alts);
        Assert.Equal(2, alts!.Count);
        Assert.Equal(CheddarSharpId.ToString(), alts[0].ProductId);
        Assert.Equal("Cheddar, Sharp", alts[0].ProductName); // catalog name, not "STALE SHARP NAME"
        Assert.Equal(0.72m, alts[0].Confidence);
        Assert.Equal("Cheddar, Marble", alts[1].ProductName);
    }

    // ── Estimate presence rules ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Estimate present only when weight + weight-unit + each-count are all set")]
    public void Estimate_Present_When_All_Fields_Set()
    {
        var line = Line(
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);

        var estimate = Build(Session([line])).Lines[0].Estimate;

        Assert.NotNull(estimate);
        Assert.Equal(7m, estimate!.EachCount);
        Assert.Equal(1.34m, estimate.Weight);
        Assert.Equal("lb", estimate.WeightUnit);
        Assert.Equal("High", estimate.Confidence);
    }

    [Fact(DisplayName = "Estimate absent when the each-count is missing")]
    public void Estimate_Absent_When_EachCount_Missing()
    {
        var line = Line(receiptWeight: 1.34m, receiptWeightUnitLabel: "lb", estimatedEachCount: null);

        Assert.Null(Build(Session([line])).Lines[0].Estimate);
    }

    [Fact(DisplayName = "Estimate absent when the receipt weight is missing")]
    public void Estimate_Absent_When_Weight_Missing()
    {
        var line = Line(receiptWeight: null, receiptWeightUnitLabel: "lb", estimatedEachCount: 7m);

        Assert.Null(Build(Session([line])).Lines[0].Estimate);
    }

    [Fact(DisplayName = "Estimate confidence falls back to Low when the LLM confidence is null")]
    public void Estimate_Confidence_Falls_Back_To_Low()
    {
        var line = Line(
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: null);

        Assert.Equal("Low", Build(Session([line])).Lines[0].Estimate!.Confidence);
    }

    // ── Merchant-text fallback ─────────────────────────────────────────────────────────

    [Theory(DisplayName = "Blank merchant text falls back to \"Receipt\"")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MerchantText_Blank_Falls_Back_To_Receipt(string? merchant)
    {
        var hydration = Build(Session([Line()], merchantText: merchant));

        Assert.Equal("Receipt", hydration.MerchantText);
    }

    [Fact(DisplayName = "Non-blank merchant text passes through unchanged")]
    public void MerchantText_Present_Passes_Through()
    {
        var hydration = Build(Session([Line()], merchantText: "Test Grocer"));

        Assert.Equal("Test Grocer", hydration.MerchantText);
    }

    // ── Prefill wiring ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "A pending matched line's server-side prefill is wired onto the row")]
    public void Prefill_Is_Computed_Server_Side_For_Pending_Line()
    {
        // Pending, AI-matched to Milk with a receipt unit label + quantity + price: the priority chain
        // (ReviewPrefill.ComputePrefill) should surface the suggested product, quantity, unit-by-label,
        // default location, price, and today+DefaultDueDays expiry.
        var line = Line(
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);

        var prefill = Build(Session([line])).Lines[0].Prefill;

        Assert.Equal(MilkId.ToString(), prefill.ProductId);
        Assert.Equal("Milk", prefill.ProductName);
        Assert.Equal(2m, prefill.Quantity);
        Assert.Equal(LitreUnitId.ToString(), prefill.UnitId);          // resolved from the "L" label
        Assert.Equal(FridgeLocationId.ToString(), prefill.LocationId); // product default
        Assert.Equal(3.99m, prefill.Price);
        Assert.Equal(Today.AddDays(7).ToString("yyyy-MM-dd"), prefill.Expiry); // today + DefaultDueDays
    }

    [Fact(DisplayName = "Prefill matches ReviewPrefill.ComputePrefill exactly (chain not re-implemented)")]
    public void Prefill_Delegates_To_ReviewPrefill()
    {
        var line = Line(
            suggestedProductId: MilkId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);
        var reference = Reference();

        var expected = ReviewPrefill.ComputePrefill(
            line,
            reference.Units.ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase),
            reference.Products.ToDictionary(p => p.Id, p => p.Name),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultLocationId),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultUnitId),
            reference.Products.ToDictionary(p => p.Id, p => p.DefaultDueDays),
            Today);

        var prefill = Builder.Build(
            new SessionReviewView(Guid.NewGuid(), ImportStatus.Ready, "M", null, Now, [line], reference),
            Today, Now, Urls).Lines[0].Prefill;

        Assert.Equal(expected.ProductId?.ToString(), prefill.ProductId);
        Assert.Equal(expected.ProductName, prefill.ProductName);
        Assert.Equal(expected.Qty, prefill.Quantity);
        Assert.Equal(expected.UnitId?.ToString(), prefill.UnitId);
        Assert.Equal(expected.LocationId?.ToString(), prefill.LocationId);
        Assert.Equal(expected.Price, prefill.Price);
        Assert.Equal(expected.Expiry?.ToString("yyyy-MM-dd"), prefill.Expiry);
    }

    // ── Header / passthrough wiring ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Handler URLs are threaded verbatim into the payload")]
    public void Handler_Urls_Are_Passed_Through()
    {
        var h = Build(Session([Line()]));

        Assert.Equal("/c", h.CommitUrl);
        Assert.Equal("/d", h.DiscardUrl);
        Assert.Equal("/s", h.SaveLineUrl);
        Assert.Equal("/di", h.DismissLineUrl);
        Assert.Equal("/r", h.RestoreLineUrl);
        Assert.Equal("/ro", h.ReopenLineUrl);
        Assert.Equal("/cl", h.ConfirmLinesUrl);
    }

    [Fact(DisplayName = "ScanVia maps a Receipt source to \"photo\"")]
    public void ScanVia_Receipt_Is_Photo()
    {
        var h = Build(Session([Line()], sourceType: ImportSourceType.Receipt));

        Assert.Equal("photo", h.ScanVia);
    }

    [Fact(DisplayName = "Today is emitted as an ISO date and reference data is projected")]
    public void Header_And_Reference_Are_Projected()
    {
        var h = Build(Session([Line()]));

        Assert.Equal("2026-06-15", h.Today);
        Assert.Equal(3, h.Products.Count);
        Assert.Equal(2, h.Units.Count);
        Assert.Single(h.Locations);
        Assert.Single(h.Categories);
        // A product default expiry is today + DefaultDueDays (Milk: 7 days).
        var milk = h.Products.Single(p => p.Id == MilkId.ToString());
        Assert.Equal(Today.AddDays(7).ToString("yyyy-MM-dd"), milk.Defaults.Expiry);
    }
}
