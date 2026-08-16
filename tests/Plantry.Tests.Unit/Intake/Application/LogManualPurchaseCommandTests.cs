using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1/L2 tests for <see cref="LogManualPurchaseCommand"/> (plantry-45ba.2) — the typed-purchase entry point
/// that reuses the receipt-intake domain end to end: start → mark ready → correct header → confirm lines →
/// delegate to the unchanged <see cref="CommitSessionCommand"/>.
/// </summary>
public sealed class LogManualPurchaseCommandTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _unitId = Guid.CreateVersion7();
    private readonly Guid _locationId = Guid.CreateVersion7();
    private readonly Guid _categoryId = Guid.CreateVersion7();
    private static readonly DateOnly PurchaseDate = new(2026, 7, 30);

    private LogManualPurchaseCommand Command(
        IReadOnlyList<ManualPurchaseLineInput> lines,
        FakeImportSessionRepository repo,
        FakeCreateProductPort? create = null,
        FakeAddStockPort? add = null,
        FakeRecordPricePort? price = null,
        FakeEnsurePurchaseStorePort? store = null,
        FakeReviewReferenceDataProvider? reference = null,
        FakeSeedConversionPort? seed = null,
        string? merchantText = null,
        Guid? selectedStoreId = null) =>
        new(_userId, merchantText, selectedStoreId, PurchaseDate, lines,
            repo, create ?? new(), add ?? new(), price ?? new(), store ?? new(),
            reference ?? new FakeReviewReferenceDataProvider(), seed ?? new(), Clock,
            new FakeTenantContext(_household), NullLogger<CommitSessionCommand>.Instance,
            NullLogger<LogManualPurchaseCommand>.Instance);

    private ReviewReferenceData ReferenceWithProduct(Guid productId, string name) =>
        new([new ReviewProductOption(productId, name, "ea", _unitId, _locationId, [])], [], [], [], []);

    [Fact]
    public async Task Commits_A_Multi_Line_Manual_Purchase_With_Stock_And_Price_Backdated()
    {
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Flour");
        var lines = new List<ManualPurchaseLineInput>
        {
            new(productId, null, null, 2m, _unitId, _locationId, Price: 4.99m),
            new(productId, null, null, 1m, _unitId, _locationId, Price: 2.50m),
        };

        var repo = new FakeImportSessionRepository();
        var add = new FakeAddStockPort();
        var price = new FakeRecordPricePort();

        var result = await Command(
            lines, repo, add: add, price: price,
            reference: new FakeReviewReferenceDataProvider(reference), merchantText: "Corner Store").ExecuteAsync();

        Assert.True(result.IsSuccess);
        var session = Assert.Single(repo.Sessions);
        Assert.Equal(ImportSourceType.Manual, session.SourceType);
        Assert.Equal(ImportStatus.Committed, session.Status);
        Assert.Equal(2, add.ProductIds.Count);
        Assert.Equal([4.99m, 2.50m], price.Prices);
        Assert.All(price.MerchantTexts, m => Assert.Equal("Corner Store", m));
        Assert.All(add.PurchasedAts, at => Assert.Equal(PurchaseDate, at)); // backdated to the typed purchase date
        Assert.All(session.Lines, l => Assert.Equal(LineStatus.Committed, l.Status));
        Assert.All(session.Lines, l => Assert.Equal("Flour", l.ReceiptText)); // receiptText = the resolved product name
    }

    [Fact]
    public async Task A_Line_With_No_Price_Commits_Stock_And_Writes_No_Observation()
    {
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Free sample");
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: null) };

        var repo = new FakeImportSessionRepository();
        var add = new FakeAddStockPort();
        var price = new FakeRecordPricePort();

        var result = await Command(
            lines, repo, add: add, price: price, reference: new FakeReviewReferenceDataProvider(reference)).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(add.ProductIds);
        Assert.Empty(price.Prices);
    }

    [Fact]
    public async Task A_Line_Naming_A_New_Product_Creates_It_And_Stocks_It()
    {
        var lines = new List<ManualPurchaseLineInput>
        {
            new(null, "Artisan sourdough", _categoryId, 1m, _unitId, _locationId, Price: 6.00m),
        };

        var repo = new FakeImportSessionRepository();
        var create = new FakeCreateProductPort();
        var add = new FakeAddStockPort();

        var result = await Command(lines, repo, create: create, add: add).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var created = Assert.Single(create.Calls);
        Assert.Equal("Artisan sourdough", created.Name);
        Assert.Equal(_categoryId, created.CategoryId);
        Assert.Single(add.ProductIds);

        var session = Assert.Single(repo.Sessions);
        var line = Assert.Single(session.Lines);
        Assert.Equal("Artisan sourdough", line.ReceiptText); // receiptText = the typed new-product name
        Assert.NotNull(line.CreatedProductId);
    }

    [Fact]
    public async Task A_Typed_Store_Name_Find_Or_Creates()
    {
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Flour");
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: 3.00m) };

        var repo = new FakeImportSessionRepository();
        var store = new FakeEnsurePurchaseStorePort();

        var result = await Command(
            lines, repo, store: store, reference: new FakeReviewReferenceDataProvider(reference),
            merchantText: "Corner Store").ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("Corner Store", Assert.Single(store.Calls));
    }

    [Fact]
    public async Task A_Picked_Store_Id_Is_Used_Directly_With_No_Name_Round_Trip()
    {
        var productId = Guid.CreateVersion7();
        var storeId = Guid.CreateVersion7();
        var reference = new ReviewReferenceData(
            [new ReviewProductOption(productId, "Flour", "ea", _unitId, _locationId, [])],
            [], [], [], [new ReviewStoreOption(storeId, "Corner Store")]);
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: 3.00m) };

        var repo = new FakeImportSessionRepository();
        var store = new FakeEnsurePurchaseStorePort();
        var price = new FakeRecordPricePort();

        var result = await Command(
            lines, repo, store: store, price: price, reference: new FakeReviewReferenceDataProvider(reference),
            merchantText: "Corner Store", selectedStoreId: storeId).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(store.Calls); // no name find-or-create for a picked store
        Assert.Equal(storeId, Assert.Single(price.StoreIds));
    }

    [Fact]
    public async Task A_Priced_Purchase_With_No_Store_Commits_With_A_Null_Store_And_No_Find_Or_Create()
    {
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Flour");
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: 3.00m) };

        var repo = new FakeImportSessionRepository();
        var store = new FakeEnsurePurchaseStorePort();
        var price = new FakeRecordPricePort();

        var result = await Command(
            lines, repo, store: store, price: price,
            reference: new FakeReviewReferenceDataProvider(reference), merchantText: null).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(store.Calls);
        Assert.Null(Assert.Single(price.StoreIds));
        Assert.Null(Assert.Single(price.MerchantTexts));
    }

    [Fact]
    public async Task Rejects_A_Stale_Or_Unknown_Selected_Store_Id()
    {
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Flour"); // no stores at all
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId) };

        var repo = new FakeImportSessionRepository();

        var result = await Command(
            lines, repo, reference: new FakeReviewReferenceDataProvider(reference),
            selectedStoreId: Guid.CreateVersion7()).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.UnknownStore", result.Error.Code);
        Assert.Empty(repo.Sessions); // rejected before the session was even started
    }

    [Fact]
    public async Task Rejects_An_Unknown_Or_Stale_Product_Id()
    {
        var reference = new ReviewReferenceData([], [], [], [], []); // no products at all
        var lines = new List<ManualPurchaseLineInput> { new(Guid.CreateVersion7(), null, null, 1m, _unitId, _locationId, Price: 3.00m) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo, reference: new FakeReviewReferenceDataProvider(reference)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.UnknownProduct", result.Error.Code);
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Rejects_Zero_Lines()
    {
        var repo = new FakeImportSessionRepository();

        var result = await Command([], repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.NoLines", result.Error.Code);
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Rejects_A_Non_Positive_Quantity()
    {
        var productId = Guid.CreateVersion7();
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 0m, _unitId, _locationId) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidQuantity", result.Error.Code);
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Rejects_A_Negative_Price()
    {
        var productId = Guid.CreateVersion7();
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: -1m) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidPrice", result.Error.Code);
    }

    [Fact]
    public async Task Rejects_A_Line_With_Neither_An_Existing_Nor_A_New_Product()
    {
        var lines = new List<ManualPurchaseLineInput> { new(null, null, null, 1m, _unitId, _locationId) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidLineProduct", result.Error.Code);
    }

    [Fact]
    public async Task Rejects_A_Line_With_Both_An_Existing_And_A_New_Product()
    {
        var productId = Guid.CreateVersion7();
        var lines = new List<ManualPurchaseLineInput> { new(productId, "Also new", _categoryId, 1m, _unitId, _locationId) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidLineProduct", result.Error.Code);
    }

    [Fact]
    public async Task Allows_A_New_Product_Line_With_No_Category()
    {
        var lines = new List<ManualPurchaseLineInput> { new(null, "Mystery item", null, 1m, _unitId, _locationId) };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Value);
    }

    [Fact]
    public async Task Validation_Failure_Starts_No_Session_At_All()
    {
        // Atomicity: a bad line among several good ones fails the WHOLE submission before any line is added.
        var productId = Guid.CreateVersion7();
        var lines = new List<ManualPurchaseLineInput>
        {
            new(productId, null, null, 1m, _unitId, _locationId, Price: 3.00m),
            new(null, null, null, 1m, _unitId, _locationId), // neither existing nor new
        };
        var repo = new FakeImportSessionRepository();

        var result = await Command(lines, repo).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Failure_Mid_Commit_Names_How_Many_Lines_Committed_And_Session_Stays_Visible()
    {
        var productA = Guid.CreateVersion7();
        var productB = Guid.CreateVersion7();
        var reference = new ReviewReferenceData(
            [
                new ReviewProductOption(productA, "Flour", "ea", _unitId, _locationId, []),
                new ReviewProductOption(productB, "Sugar", "ea", _unitId, _locationId, []),
            ], [], [], [], []);
        var lines = new List<ManualPurchaseLineInput>
        {
            new(productA, null, null, 1m, _unitId, _locationId, Price: 2m),
            new(productB, null, null, 1m, _unitId, _locationId, Price: 3m),
        };

        var repo = new FakeImportSessionRepository();
        var add = new FakeAddStockPort { FailOnCall = 2 }; // first line commits, second throws mid-batch

        var result = await Command(
            lines, repo, add: add, reference: new FakeReviewReferenceDataProvider(reference)).ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.CommitFailed", result.Error.Code);
        Assert.Contains("1 of 2 line(s) were committed", result.Error.Description);
        Assert.Contains("remains in Intake history", result.Error.Description);

        var session = Assert.Single(repo.Sessions);
        Assert.Equal(ImportStatus.Ready, session.Status); // not marked committed — resumable, stays visible
        Assert.Equal(1, session.Lines.Count(l => l.Status == LineStatus.Committed));
    }

    [Fact]
    public async Task Rejects_An_Unset_Purchase_Date()
    {
        // The web page guards against a cleared/malformed date, but this is the load-bearing check for
        // any caller — an omitted key leaves the bound DateOnly at its default with no ModelState error
        // to catch it, so the command itself must reject default(DateOnly) before starting a session.
        var productId = Guid.CreateVersion7();
        var reference = ReferenceWithProduct(productId, "Flour");
        var lines = new List<ManualPurchaseLineInput> { new(productId, null, null, 1m, _unitId, _locationId, Price: 3.00m) };
        var repo = new FakeImportSessionRepository();

        var cmd = new LogManualPurchaseCommand(
            _userId, "Corner Store", null, purchaseDate: default, lines, repo,
            new FakeCreateProductPort(), new FakeAddStockPort(), new FakeRecordPricePort(),
            new FakeEnsurePurchaseStorePort(), new FakeReviewReferenceDataProvider(reference),
            new FakeSeedConversionPort(), Clock, new FakeTenantContext(_household),
            NullLogger<CommitSessionCommand>.Instance, NullLogger<LogManualPurchaseCommand>.Instance);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.MissingPurchaseDate", result.Error.Code);
        Assert.Empty(repo.Sessions);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var repo = new FakeImportSessionRepository();
        var lines = new List<ManualPurchaseLineInput> { new(null, "Mystery item", _categoryId, 1m, _unitId, _locationId) };

        var cmd = new LogManualPurchaseCommand(
            _userId, null, null, PurchaseDate, lines, repo, new FakeCreateProductPort(), new FakeAddStockPort(),
            new FakeRecordPricePort(), new FakeEnsurePurchaseStorePort(), new FakeReviewReferenceDataProvider(),
            new FakeSeedConversionPort(), Clock, new FakeTenantContext(null),
            NullLogger<CommitSessionCommand>.Instance, NullLogger<LogManualPurchaseCommand>.Instance);

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Sessions);
    }
}
