using System.Text.Json;
using Plantry.Market.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 WAF tests for the Intake review page's price-delta trip-context stat (plantry-bb7p) — the glue in
/// <c>ReviewModel.ComputePriceStatsAsync</c> (the <c>LatestForProductsAsync</c> call, the
/// <c>UnitPrice is &gt; 0m</c> filter, the <c>TryNormalizeAsync</c> loop, and the
/// <c>priceDeltaPercent</c> key on both the GET hydration and the SaveLine JSON response). The pure
/// combination rules are already pinned by <c>IntakeLinePriceDeltasTests</c>; these tests exercise the
/// impure wiring around it end-to-end through the real page pipeline, seeding
/// <see cref="ReviewFragmentFactory.PriceObservations"/>/<see cref="ReviewFragmentFactory.UnitPriceCalculator"/>
/// (which the factory otherwise wires empty/null, per its own doc comment, precisely so this class can
/// override them). Each test constructs its own <see cref="ReviewFragmentFactory"/> — same convention the
/// mutating SaveLine tests in <see cref="ReviewBoundaryTests"/> use — so no seed or SaveLine-induced state
/// change leaks between tests.
/// </summary>
public sealed class ReviewPriceDeltaTests
{
    [Fact(DisplayName = "GET hydration carries the computed priceDeltaPercent for a Confirmed line with prior price history")]
    public async Task Get_Hydration_Carries_PriceDeltaPercent()
    {
        using var f = new ReviewFragmentFactory();
        // The "FREE RANGE EGGS" fixture line (Confirmed, Eggs, qty 12, price 4.50) is the only
        // Confirmed-with-a-resolved-product line in the fixture, so seeding Eggs' history and making the
        // calculator normalize every line's own price to 2.24 deterministically yields
        // (2.24 - 2.00) / 2.00 = 0.12 for it and nothing else.
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.EggsProductId, skuId: null,
            price: 4.00m, quantity: 2m, unitId: ReviewSessionFixture.EachUnitId,
            unitPrice: 2.00m, source: PriceSource.Purchase, merchantText: "Fresh Mart", sourceRef: null,
            observedAt: DateTimeOffset.UtcNow.AddDays(-14), userId: Guid.NewGuid()));
        f.UnitPriceCalculator.ReturnValue = 2.24m;

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.Equal(0.12m, eggsLine.GetProperty("line").GetProperty("priceDeltaPercent").GetDecimal());
    }

    [Fact(DisplayName = "GET hydration carries no priceDeltaPercent when there is no prior price history")]
    public async Task Get_Hydration_Has_No_Delta_Without_History()
    {
        using var f = new ReviewFragmentFactory();
        // No seeded observation; UnitPriceCalculator.ReturnValue defaults to null (soft-fail).

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.Equal(JsonValueKind.Null, eggsLine.GetProperty("line").GetProperty("priceDeltaPercent").ValueKind);
    }

    [Fact(DisplayName = "SaveLine's JSON response carries the just-resolved line's priceDeltaPercent")]
    public async Task SaveLine_Response_Carries_PriceDeltaPercent()
    {
        using var f = new ReviewFragmentFactory();
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Purchase, merchantText: "Fresh Mart", sourceRef: null,
            observedAt: DateTimeOffset.UtcNow.AddDays(-7), userId: Guid.NewGuid()));
        f.UnitPriceCalculator.ReturnValue = 1.84m; // (1.84 - 2.00) / 2.00 = -0.08

        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var root = await ReviewBoundaryTests.PostSaveLineAsync(f, new
        {
            lineId = milkLineId, createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            price = 3.68m,
        });

        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.Equal(-0.08m, root.GetProperty("priceDeltaPercent").GetDecimal());
    }

    [Fact(DisplayName = "GET hydration marks dealHit true when the line's price matches an active confirmed deal at the picked store/date")]
    public async Task Get_Hydration_Marks_DealHit_True_When_Deal_Matches()
    {
        using var f = new ReviewFragmentFactory();
        // ComputeDealHitsAsync needs a picked store + purchase date to check against — the fixture session
        // has neither by default (only StoreBranch/PurchaseDate display metadata, no SelectedStoreId), so
        // this test drives the same CorrectHeader transition the review header-edit flow uses.
        f.SessionA.CorrectHeader(
            "Fresh Mart", ReviewSessionFixture.FreshMartStoreId,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);

        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.EggsProductId, skuId: null,
            price: 4.00m, quantity: 2m, unitId: ReviewSessionFixture.EachUnitId,
            unitPrice: 2.30m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 2.24m; // at-or-below the 2.30 deal price → qualifies

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.True(eggsLine.GetProperty("line").GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "GET hydration marks dealHit false when the line's price exceeds the deal price beyond tolerance")]
    public async Task Get_Hydration_Marks_DealHit_False_When_Price_Exceeds_Tolerance()
    {
        using var f = new ReviewFragmentFactory();
        f.SessionA.CorrectHeader(
            "Fresh Mart", ReviewSessionFixture.FreshMartStoreId,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);

        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.EggsProductId, skuId: null,
            price: 4.00m, quantity: 2m, unitId: ReviewSessionFixture.EachUnitId,
            unitPrice: 2.00m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 2.24m; // above the 2.00 deal price by more than the 1% tolerance

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.False(eggsLine.GetProperty("line").GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "SaveLine's JSON response marks dealHit true when the just-resolved line's price matches an active confirmed deal")]
    public async Task SaveLine_Response_Marks_DealHit_True_When_Deal_Matches()
    {
        using var f = new ReviewFragmentFactory();
        // Like the GET dealHit tests above, ComputeDealHitsAsync needs a picked store + purchase date —
        // drive the same CorrectHeader transition the review header-edit flow uses.
        f.SessionA.CorrectHeader(
            "Fresh Mart", ReviewSessionFixture.FreshMartStoreId,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);

        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 1.99m; // at-or-below the 2.00 deal price → qualifies

        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var root = await ReviewBoundaryTests.PostSaveLineAsync(f, new
        {
            lineId = milkLineId, createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            price = 3.98m,
        });

        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "SaveLine's JSON response marks dealHit false when the line's price exceeds the deal price beyond tolerance")]
    public async Task SaveLine_Response_Marks_DealHit_False_When_Price_Exceeds_Tolerance()
    {
        using var f = new ReviewFragmentFactory();
        f.SessionA.CorrectHeader(
            "Fresh Mart", ReviewSessionFixture.FreshMartStoreId,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);

        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 2.24m; // above the 2.00 deal price by more than the 1% tolerance

        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var root = await ReviewBoundaryTests.PostSaveLineAsync(f, new
        {
            lineId = milkLineId, createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            price = 4.48m,
        });

        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "GET hydration marks dealHit true from the AI-parsed merchant text alone — no explicit store pick needed")]
    public async Task Get_Hydration_Marks_DealHit_From_MerchantText_Fallback()
    {
        using var f = new ReviewFragmentFactory();
        // No store pick: selectedStoreId stays null, so ComputeDealHitsAsync must resolve the store by
        // matching the merchant text against the household's stores — the dominant scanned-receipt flow,
        // where the AI parse names the merchant but the user never opens the header editor. The messy
        // whitespace exercises the same normalize-and-compare EnsureStoreByNameCommand applies at commit.
        f.SessionA.CorrectHeader(
            "  Fresh   Mart ", selectedStoreId: null,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);

        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.EggsProductId, skuId: null,
            price: 4.00m, quantity: 2m, unitId: ReviewSessionFixture.EachUnitId,
            unitPrice: 2.30m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 2.24m; // at-or-below the 2.30 deal price → qualifies

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.True(eggsLine.GetProperty("line").GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "GET hydration marks dealHit false when the merchant text matches no household store — a GET never mints a Store row")]
    public async Task Get_Hydration_Marks_DealHit_False_For_Unknown_Merchant()
    {
        using var f = new ReviewFragmentFactory();
        // Untouched fixture header: merchant text "Test Grocer" (no such store in reference data), no
        // store pick, purchase date 2026-06-15 from the receipt metadata. A qualifying deal exists at
        // Fresh Mart, but with no resolvable store nothing may preview as a hit.
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.EggsProductId, skuId: null,
            price: 4.00m, quantity: 2m, unitId: ReviewSessionFixture.EachUnitId,
            unitPrice: 2.30m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 2.24m; // would qualify if the store resolved

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, ReviewSessionFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Intake/Review/{f.SessionAId}")).Content.ReadAsStringAsync();

        var eggsLine = ExtractLine(html, "FREE RANGE EGGS");
        Assert.False(eggsLine.GetProperty("line").GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "ConfirmLines' JSON response carries each bulk-confirmed line's priceDeltaPercent and dealHit")]
    public async Task ConfirmLines_Response_Carries_PerLine_Stats()
    {
        using var f = new ReviewFragmentFactory();
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Purchase, merchantText: "Fresh Mart", sourceRef: null,
            observedAt: DateTimeOffset.UtcNow.AddDays(-7), userId: Guid.NewGuid()));
        f.UnitPriceCalculator.ReturnValue = 1.84m; // (1.84 - 2.00) / 2.00 = -0.08

        // WHOLE MILK 2L is Pending, High-confidence, with a complete server-side prefill — the same
        // qualifying line ReviewBoundaryTests' ConfirmLines tests use.
        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var root = await ReviewBoundaryTests.PostJsonHandlerAsync(f, "ConfirmLines", new
        {
            lineIds = new[] { milkLineId },
        });

        Assert.Equal("Confirmed", root.GetProperty("status").GetString());
        var entry = root.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetString() == milkLineId.ToString());
        Assert.Equal(-0.08m, entry.GetProperty("priceDeltaPercent").GetDecimal());
        Assert.False(entry.GetProperty("dealHit").GetBoolean()); // no store resolvable, no deal seeded
    }

    [Fact(DisplayName = "CorrectHeader's JSON response recomputes per-line stats — picking the deal's store flips dealHit true without a reload")]
    public async Task CorrectHeader_Response_Refreshes_DealHit_To_True()
    {
        using var f = new ReviewFragmentFactory();
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 1.99m; // qualifies once the store resolves

        // Resolve the milk line with NO store resolvable (fixture merchant "Test Grocer") — no hit yet.
        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var saveRoot = await ReviewBoundaryTests.PostSaveLineAsync(f, new
        {
            lineId = milkLineId, createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            price = 3.98m,
        });
        Assert.False(saveRoot.GetProperty("dealHit").GetBoolean());

        // Correct the header to the deal's store: the response must carry the recomputed stats.
        var root = await ReviewBoundaryTests.PostJsonHandlerAsync(f, "CorrectHeader", new
        {
            merchantText = "Fresh Mart", selectedStoreId = ReviewSessionFixture.FreshMartStoreId,
            purchaseDate = "2026-06-15", purchaseTime = "14:30",
        });

        var entry = root.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetString() == milkLineId.ToString());
        Assert.True(entry.GetProperty("dealHit").GetBoolean());
    }

    [Fact(DisplayName = "CorrectHeader's JSON response actively clears dealHit when the store is corrected away from the deal's store")]
    public async Task CorrectHeader_Response_Clears_DealHit_When_Store_Corrected_Away()
    {
        using var f = new ReviewFragmentFactory();
        f.PriceObservations.Items.Add(PriceObservation.Record(
            HouseholdId.From(ReviewSessionFixture.HouseholdAId),
            ReviewSessionFixture.MilkProductId, skuId: null,
            price: 2.00m, quantity: 1m, unitId: ReviewSessionFixture.LitreUnitId,
            unitPrice: 2.00m, source: PriceSource.Deal, merchantText: null, sourceRef: Guid.NewGuid(),
            observedAt: DateTimeOffset.UtcNow.AddDays(-3), userId: Guid.NewGuid(),
            validFrom: new DateOnly(2026, 6, 1), validTo: new DateOnly(2026, 6, 30),
            storeId: ReviewSessionFixture.FreshMartStoreId));
        f.UnitPriceCalculator.ReturnValue = 1.99m;

        // Pick the deal's store and resolve the line — dealHit true on the SaveLine response.
        f.SessionA.CorrectHeader(
            "Fresh Mart", ReviewSessionFixture.FreshMartStoreId,
            new DateOnly(2026, 6, 15), new TimeOnly(14, 30), SystemClock.Instance);
        var milkLineId = f.SessionA.Lines.Single(l => l.ReceiptText == "WHOLE MILK 2L").Id.Value;
        var saveRoot = await ReviewBoundaryTests.PostSaveLineAsync(f, new
        {
            lineId = milkLineId, createNew = false, productId = ReviewSessionFixture.MilkProductId,
            quantity = 2m, unitId = ReviewSessionFixture.LitreUnitId, locationId = ReviewSessionFixture.FridgeLocationId,
            price = 3.98m,
        });
        Assert.True(saveRoot.GetProperty("dealHit").GetBoolean());

        // Correct the store AWAY (unknown merchant, no explicit pick): the previously-true marker must be
        // actively cleared in the response, not left stale until a full page load.
        var root = await ReviewBoundaryTests.PostJsonHandlerAsync(f, "CorrectHeader", new
        {
            merchantText = "Test Grocer", selectedStoreId = (Guid?)null,
            purchaseDate = "2026-06-15", purchaseTime = "14:30",
        });

        var entry = root.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("lineId").GetString() == milkLineId.ToString());
        Assert.False(entry.GetProperty("dealHit").GetBoolean());
    }

    /// <summary>Finds one line's hydration entry by its <c>receiptText</c> via the shared
    /// <see cref="ReviewFragmentSnapshotTests"/> scrape helpers (single copy of the regex-and-parse).</summary>
    private static JsonElement ExtractLine(string html, string receiptText) =>
        ReviewFragmentSnapshotTests.FindLine(
            ReviewFragmentSnapshotTests.ExtractHydrationRoot(html).GetProperty("lines"), receiptText);
}
