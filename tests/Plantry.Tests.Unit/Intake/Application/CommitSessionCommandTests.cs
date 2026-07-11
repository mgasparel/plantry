using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1/L2 tests for <see cref="CommitSessionCommand"/> over fake ports — the per-line, cross-context
/// commit orchestration (ADR-010): only confirmed lines write, new products are created on the fly, and
/// a mid-batch failure is resumable without double-writing the lines that already committed.
/// </summary>
public sealed class CommitSessionCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();

    private ImportSession ReadySession()
    {
        var session = ImportSession.Start(HouseholdId.From(_household), ImportSourceType.Receipt, _userId, Clock);
        return session;
    }

    private CommitSessionCommand Commit(
        ImportSession session, FakeImportSessionRepository repo,
        FakeCreateProductPort create, FakeAddStockPort add, FakeRecordPricePort price,
        FakeEnsurePurchaseStorePort? store = null,
        FakeReviewReferenceDataProvider? reference = null,
        FakeSeedConversionPort? seed = null) =>
        new(session.Id, repo, create, add, price, store ?? new FakeEnsurePurchaseStorePort(),
            reference ?? new FakeReviewReferenceDataProvider(), seed ?? new FakeSeedConversionPort(),
            Clock, new FakeTenantContext(_household), NullLogger<CommitSessionCommand>.Instance);

    [Fact]
    public async Task Commits_A_Confirmed_Existing_Product_Line_With_Stock_And_Price()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, """{"x":1}""");
        session.MarkReady("Superstore", Clock.UtcNow);
        var productId = Guid.CreateVersion7();
        line.Confirm(productId, skuId: null, 1m, _unitId, _locationId, expiryDate: null, price: 4.99m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var create = new FakeCreateProductPort();
        var add = new FakeAddStockPort();
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, create, add, price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(create.Calls);                      // existing product, no create
        Assert.Equal(productId, Assert.Single(add.ProductIds));
        Assert.Equal(4.99m, Assert.Single(price.Prices));
        Assert.Equal(LineStatus.Committed, line.Status);
        Assert.NotNull(line.JournalId);
        Assert.NotNull(line.PriceObservationId);
        Assert.Equal(ImportStatus.Committed, session.Status);
    }

    [Fact]
    public async Task Only_Confirmed_Lines_Commit_Dismissed_Are_Skipped()
    {
        // A dismissed line is skipped (not stocked, not blocked); a confirmed line commits. (A Pending line
        // at commit blocks the whole commit — covered separately.)
        var session = ReadySession();
        var confirmed = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var dismissed = session.AddLine(2, "Loyalty points", SuggestedConfidence.None, null);
        session.MarkReady(null, Clock.UtcNow);
        confirmed.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 2.50m);
        dismissed.Dismiss();

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, new(), add, new()).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(add.ProductIds);                   // only the confirmed line
        Assert.Equal(LineStatus.Committed, confirmed.Status);
        Assert.Equal(LineStatus.Dismissed, dismissed.Status); // skipped, unchanged
    }

    // ── Strict commit gate — any still-Pending line blocks the commit (plantry-gpdb) ────────────────
    // The deck-flow review confirms sure things (ConfirmLinesCommand) and resolves the deck up front; the
    // commit-time auto-confirm pre-pass is removed. A Pending line at commit is genuinely unresolved and
    // fails the whole commit, naming the offending line — never silently skipped, never half-committed.

    [Fact]
    public async Task Blocks_Commit_When_A_High_Confidence_Pending_Line_Remains_No_Auto_Confirm()
    {
        // A High-confidence line with a full suggested product/qty/unit — which the removed commit-time
        // auto-confirm pre-pass used to promote silently — is now left Pending and BLOCKS the commit. The
        // user must confirm it first via the deck-flow "Confirm N matches" bulk action (ConfirmLinesCommand).
        var session = ReadySession();
        var confirmed = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var highPending = session.AddLine(2, "FLOUR 1KG", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: Guid.CreateVersion7(), suggestedQuantity: 2m, suggestedUnitLabel: "kg", suggestedPrice: 4.99m);
        session.MarkReady("Superstore", Clock.UtcNow);
        confirmed.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 2.50m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, new(), add, new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.UnresolvedLine", result.Error.Code);
        Assert.Contains("FLOUR 1KG", result.Error.Description);    // error names the offending line
        Assert.Empty(add.ProductIds);                              // nothing stocked — never half-committed
        Assert.Equal(ImportStatus.Ready, session.Status);          // session not marked committed
        Assert.Equal(LineStatus.Confirmed, confirmed.Status);      // the confirmed line was not committed either
        Assert.Equal(LineStatus.Pending, highPending.Status);      // high line left Pending, NOT auto-confirmed
    }

    [Fact]
    public async Task Blocks_Commit_When_A_Low_Confidence_Pending_Line_Remains()
    {
        var session = ReadySession();
        var confirmed = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var lowPending = session.AddLine(2, "MYSTERY ITEM", SuggestedConfidence.Low, null,
            suggestedProductId: Guid.CreateVersion7(), suggestedQuantity: 1m, suggestedUnitLabel: "kg");
        session.MarkReady(null, Clock.UtcNow);
        confirmed.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 2.50m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, new(), add, new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.UnresolvedLine", result.Error.Code);
        Assert.Contains("MYSTERY ITEM", result.Error.Description); // error names the offending line
        Assert.Empty(add.ProductIds);                              // nothing stocked — never half-committed
        Assert.Equal(ImportStatus.Ready, session.Status);          // session not marked committed
        Assert.Equal(LineStatus.Confirmed, confirmed.Status);      // the confirmed line was not committed either
        Assert.Equal(LineStatus.Pending, lowPending.Status);       // low line left Pending
    }

    [Fact]
    public async Task Creates_A_New_Product_Before_Adding_Stock()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Artisan sourdough", SuggestedConfidence.None, null);
        session.MarkReady(null, Clock.UtcNow);
        var categoryId = Guid.CreateVersion7();
        line.ConfirmAsNew("Artisan sourdough", categoryId, 1m, _unitId, _locationId, null, 6.00m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var create = new FakeCreateProductPort();
        var add = new FakeAddStockPort();

        var result = await Commit(session, repo, create, add, new()).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var created = Assert.Single(create.Calls);
        Assert.Equal("Artisan sourdough", created.Name);
        Assert.Equal(categoryId, created.CategoryId);
        // Stock is added against the just-created product, and the line records the new product id.
        Assert.Equal(Assert.Single(add.ProductIds), line.CreatedProductId);
        Assert.NotNull(line.CreatedProductId);
    }

    [Fact]
    public async Task Records_No_Price_When_The_Line_Has_None()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Free sample", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, price: null);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, new(), new(), price).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(price.Prices);
        Assert.Null(line.PriceObservationId);
        Assert.Equal(LineStatus.Committed, line.Status);
    }

    [Fact]
    public async Task Resolves_Merchant_To_A_Store_And_Stamps_StoreId_On_The_Price_Observation()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, null);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, price: 4.99m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();
        var store = new FakeEnsurePurchaseStorePort();

        var result = await Commit(session, repo, new(), new(), price, store).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Superstore", Assert.Single(store.Calls));           // merchant resolved find-or-create
        Assert.NotNull(Assert.Single(price.StoreIds));                    // resolved store id stamped onto the observation
        Assert.Equal("Superstore", Assert.Single(price.MerchantTexts));   // MerchantText retained for provenance
    }

    [Fact]
    public async Task Blank_Merchant_Leaves_StoreId_Null_Without_Resolving_A_Store()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Flour 1kg", SuggestedConfidence.High, null);
        session.MarkReady("   ", Clock.UtcNow); // whitespace-only merchant is treated as blank
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, price: 4.99m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();
        var store = new FakeEnsurePurchaseStorePort();

        var result = await Commit(session, repo, new(), new(), price, store).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(store.Calls);                       // no ensure attempted for a blank merchant
        Assert.Null(Assert.Single(price.StoreIds));      // store_id left null
    }

    [Fact]
    public async Task Resolves_The_Store_Once_And_Reuses_It_Across_Multiple_Priced_Lines()
    {
        var session = ReadySession();
        var line1 = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var line2 = session.AddLine(2, "Milk", SuggestedConfidence.High, null);
        session.MarkReady("Superstore", Clock.UtcNow);
        line1.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, 3m);
        line2.Confirm(Guid.CreateVersion7(), null, 2m, _unitId, _locationId, null, 4m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();
        var store = new FakeEnsurePurchaseStorePort();

        var result = await Commit(session, repo, new(), new(), price, store).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(store.Calls);                          // resolved exactly once, not per line
        Assert.Equal(2, price.StoreIds.Count);
        Assert.All(price.StoreIds, id => Assert.Equal(price.StoreIds[0], id)); // same store on both lines
        Assert.NotNull(price.StoreIds[0]);
    }

    [Fact]
    public async Task Does_Not_Resolve_A_Store_When_No_Line_Has_A_Price()
    {
        var session = ReadySession();
        var line = session.AddLine(1, "Free sample", SuggestedConfidence.High, null);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(Guid.CreateVersion7(), null, 1m, _unitId, _locationId, null, price: null);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var store = new FakeEnsurePurchaseStorePort();

        var result = await Commit(session, repo, new(), new(), new(), store).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(store.Calls); // a session with no priced lines mints no store
    }

    [Fact]
    public async Task Mid_Batch_Failure_Is_Resumable_Without_Double_Writing()
    {
        var session = ReadySession();
        var line1 = session.AddLine(1, "Flour", SuggestedConfidence.High, null);
        var line2 = session.AddLine(2, "Milk", SuggestedConfidence.High, null);
        session.MarkReady(null, Clock.UtcNow);
        var product1 = Guid.CreateVersion7();
        var product2 = Guid.CreateVersion7();
        line1.Confirm(product1, null, 1m, _unitId, _locationId, null, 3m);
        line2.Confirm(product2, null, 2m, _unitId, _locationId, null, 4m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        // First run: the second line's stock write blows up mid-batch.
        var add1 = new FakeAddStockPort { FailOnCall = 2 };
        var firstRun = await Commit(session, repo, new(), add1, new()).ExecuteAsync();

        Assert.True(firstRun.IsFailure);
        Assert.Equal("Intake.CommitFailed", firstRun.Error.Code);
        Assert.Equal(LineStatus.Committed, line1.Status);   // line 1 saved before the failure
        Assert.Equal(LineStatus.Confirmed, line2.Status);   // line 2 never committed
        Assert.Equal(ImportStatus.Ready, session.Status);   // session not marked committed

        // Second run: re-commit resumes — line 1 is skipped, only line 2 is written.
        var add2 = new FakeAddStockPort();
        var secondRun = await Commit(session, repo, new(), add2, new()).ExecuteAsync();

        Assert.True(secondRun.IsSuccess);
        Assert.Equal(product2, Assert.Single(add2.ProductIds)); // line 1 NOT re-added
        Assert.Equal(LineStatus.Committed, line2.Status);
        Assert.Equal(ImportStatus.Committed, session.Status);
    }

    // ── Weight→each (plantry-1mu) ──────────────────────────────────────────────────

    private readonly Guid _lbUnitId = Guid.CreateVersion7();
    private readonly Guid _kgUnitId = Guid.CreateVersion7();
    private readonly Guid _eachUnitId = Guid.CreateVersion7();

    private FakeReviewReferenceDataProvider WeightUnitReference() =>
        new(new ReviewReferenceData(
            Products: [],
            Units:
            [
                new ReviewUnitOption(_lbUnitId, "lb", "Pound", ReviewUnitDimension.Mass),
                new ReviewUnitOption(_kgUnitId, "kg", "Kilogram", ReviewUnitDimension.Mass),
                new ReviewUnitOption(_eachUnitId, "each", "Each", ReviewUnitDimension.Count),
            ],
            Locations: [],
            Categories: []));

    /// <summary>Adds a weight-priced, each-tracked line the user accepted as an each-count.</summary>
    private ImportLine EstimatedEachLine(ImportSession session, Guid productId) =>
        session.AddLine(1, "ORG BANANAS 1.34 lb", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 1.34m, suggestedUnitLabel: "lb", suggestedPrice: 0.79m,
            receiptWeight: 1.34m, receiptWeightUnitLabel: "lb",
            estimatedEachCount: 7m, estimatedEachConfidence: SuggestedConfidence.High);

    [Fact]
    public async Task Records_The_Price_In_The_Receipt_Weight_Unit_Even_When_Stock_Committed_In_Each()
    {
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = EstimatedEachLine(session, productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 7m, _eachUnitId, _locationId, null, price: 0.79m); // accepted 7 each

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, new(), new(), price, reference: WeightUnitReference()).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0.79m, Assert.Single(price.Prices));
        Assert.Equal(1.34m, Assert.Single(price.Quantities)); // the true weight, not the 7-each count
        Assert.Equal(_lbUnitId, Assert.Single(price.UnitIds)); // $/lb — never a $/each observation
    }

    [Fact]
    public async Task Seeds_An_AiSuggested_Weight_To_Each_Conversion_When_An_Each_Count_Is_Accepted()
    {
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = EstimatedEachLine(session, productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 7m, _eachUnitId, _locationId, null, price: 0.79m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var seed = new FakeSeedConversionPort();

        var result = await Commit(session, repo, new(), new(), new(),
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var seeded = Assert.Single(seed.Seeds);
        Assert.Equal(productId, seeded.ProductId);
        Assert.Equal(_lbUnitId, seeded.FromUnitId);   // from the receipt weight unit
        Assert.Equal(_eachUnitId, seeded.ToUnitId);   // to the accepted each unit
        Assert.Equal(7m / 1.34m, seeded.Factor);      // each per lb, from confirmed count / preserved weight
    }

    [Fact]
    public async Task Does_Not_Seed_A_Conversion_When_The_User_Kept_The_Weight_Unit()
    {
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = EstimatedEachLine(session, productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        // User rejected the each interpretation and committed in lb → no factor to learn.
        line.Confirm(productId, null, 1.34m, _lbUnitId, _locationId, null, price: 0.79m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var seed = new FakeSeedConversionPort();

        var result = await Commit(session, repo, new(), new(), new FakeRecordPricePort(),
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(seed.Seeds);
    }

    [Fact]
    public async Task Does_Not_Seed_A_Conversion_For_A_Genuinely_Weight_Tracked_Line()
    {
        // Deli ham: weight-priced but no each-estimate (parser left it null) → never converted.
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "BLACK FOREST HAM 0.35 lb", SuggestedConfidence.High, null,
            suggestedProductId: productId, suggestedQuantity: 0.35m, suggestedUnitLabel: "lb", suggestedPrice: 4.20m);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 0.35m, _lbUnitId, _locationId, null, price: 4.20m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var seed = new FakeSeedConversionPort();
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, new(), new(), price,
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(seed.Seeds);
        Assert.Equal(0.35m, Assert.Single(price.Quantities)); // priced in lb as before
        Assert.Equal(_lbUnitId, Assert.Single(price.UnitIds));
    }

    [Fact]
    public async Task Does_Not_Seed_A_Conversion_When_The_User_Committed_A_Different_Weight_Unit()
    {
        // lb-priced line; the user overrides the pre-filled each-count to 0.6 kg — still a weight unit.
        // A quantity-derived "lb→kg" factor would be garbage (real cross-weight conversion is a fixed
        // constant), so nothing is seeded — yet the price still observes in the receipt's true unit (lb).
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = EstimatedEachLine(session, productId);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 0.6m, _kgUnitId, _locationId, null, price: 0.79m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var seed = new FakeSeedConversionPort();
        var price = new FakeRecordPricePort();

        var result = await Commit(session, repo, new(), new(), price,
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(seed.Seeds);                              // no bogus lb→kg conversion seeded
        Assert.Equal(1.34m, Assert.Single(price.Quantities)); // observed in the receipt weight, not 0.6
        Assert.Equal(_lbUnitId, Assert.Single(price.UnitIds)); // $/lb — the receipt's true unit
    }

    [Fact]
    public async Task Skips_The_Price_Observation_When_The_Receipt_Weight_Label_Resolves_To_No_Unit()
    {
        // Receipt weight priced in "oz" — a label no household unit matches. Recording it would fall back
        // to the committed each-unit ($/each), which plantry-1mu forbids, so the observation is skipped
        // entirely (a wrong-unit price is worse than a missing one). Stock is still added and no
        // conversion is seeded because the weight unit never resolved.
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "BULK ALMONDS 12 oz", SuggestedConfidence.High, """{"x":1}""",
            suggestedProductId: productId, suggestedQuantity: 12m, suggestedUnitLabel: "oz", suggestedPrice: 5.00m,
            receiptWeight: 12m, receiptWeightUnitLabel: "oz",
            estimatedEachCount: 3m, estimatedEachConfidence: SuggestedConfidence.High);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 3m, _eachUnitId, _locationId, null, price: 5.00m); // accepted 3 each

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var add = new FakeAddStockPort();
        var price = new FakeRecordPricePort();
        var seed = new FakeSeedConversionPort();

        var result = await Commit(session, repo, new(), add, price,
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(price.Prices);            // no wrong-unit ($/each) observation persisted
        Assert.Single(add.ProductIds);         // stock IS still added
        Assert.Empty(seed.Seeds);              // no conversion seeded — weight unit never resolved
        Assert.Null(line.PriceObservationId);  // line records no observation ref
        Assert.Equal(LineStatus.Committed, line.Status);
    }

    [Fact]
    public async Task Records_A_Non_Weight_Line_In_Its_Committed_Quantity_And_Unit()
    {
        // Fallback-path regression guard: a plain each line (no receipt weight) still observes in the
        // committed qty/unit and seeds nothing.
        var session = ReadySession();
        var productId = Guid.CreateVersion7();
        var line = session.AddLine(1, "EGGS DOZEN", SuggestedConfidence.High, null,
            suggestedProductId: productId, suggestedQuantity: 2m, suggestedUnitLabel: "each", suggestedPrice: 3.50m);
        session.MarkReady("Superstore", Clock.UtcNow);
        line.Confirm(productId, null, 2m, _eachUnitId, _locationId, null, price: 3.50m);

        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);
        var price = new FakeRecordPricePort();
        var seed = new FakeSeedConversionPort();

        var result = await Commit(session, repo, new(), new(), price,
            reference: WeightUnitReference(), seed: seed).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3.50m, Assert.Single(price.Prices));
        Assert.Equal(2m, Assert.Single(price.Quantities));       // committed quantity
        Assert.Equal(_eachUnitId, Assert.Single(price.UnitIds)); // committed unit
        Assert.Empty(seed.Seeds);                                // no receipt weight → nothing to learn
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var session = ReadySession();
        session.MarkReady(null, Clock.UtcNow);
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var cmd = new CommitSessionCommand(
            session.Id, repo, new FakeCreateProductPort(), new FakeAddStockPort(), new FakeRecordPricePort(),
            new FakeEnsurePurchaseStorePort(), new FakeReviewReferenceDataProvider(), new FakeSeedConversionPort(),
            Clock, new FakeTenantContext(null), NullLogger<CommitSessionCommand>.Instance);
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_Session_Is_Not_Ready()
    {
        var session = ReadySession(); // still Parsing
        var repo = new FakeImportSessionRepository();
        repo.Sessions.Add(session);

        var result = await Commit(session, repo, new(), new(), new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.SessionNotReady", result.Error.Code);
    }

    [Fact]
    public async Task Fails_When_Session_Not_Found()
    {
        var session = ReadySession();
        session.MarkReady(null, Clock.UtcNow);
        var repo = new FakeImportSessionRepository(); // session not added

        var result = await Commit(session, repo, new(), new(), new()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound", result.Error.Code);
    }
}
