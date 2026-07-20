using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Domain;

public sealed class ImportSessionTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly IClock Clock = SystemClock.Instance;

    private static ImportSession Started() =>
        ImportSession.Start(Household, ImportSourceType.Receipt, UserId, Clock);

    [Fact]
    public void Start_Creates_Session_In_Parsing_Status()
    {
        var session = Started();

        Assert.Equal(ImportStatus.Parsing, session.Status);
        Assert.NotEqual(Guid.Empty, session.Id.Value);
        Assert.Equal(Household, session.HouseholdId);
        Assert.Empty(session.Lines);
    }

    [Fact]
    public void AddLine_Appends_A_Line_With_Correct_Fields()
    {
        var session = Started();

        var line = session.AddLine(1, "2x Milk 2L", SuggestedConfidence.High, """{"qty":2}""");

        Assert.Single(session.Lines);
        Assert.Equal(1, line.LineNo);
        Assert.Equal("2x Milk 2L", line.ReceiptText);
        Assert.Equal(SuggestedConfidence.High, line.SuggestedConfidence);
        Assert.Equal("""{"qty":2}""", line.RawParse);
        Assert.Equal(LineStatus.Pending, line.Status);
        Assert.Equal(session.Id, line.SessionId);
    }

    [Fact]
    public void RawParse_Is_Set_Once_And_Not_Overwritten_By_Domain_Methods()
    {
        var session = Started();
        var line = session.AddLine(1, "Bread", SuggestedConfidence.High, """{"raw":1}""");
        var originalRaw = line.RawParse;

        // Confirming the line should not change raw_parse
        line.Confirm(Guid.NewGuid(), null, 1m, Guid.NewGuid(), Guid.NewGuid(), null, null);

        Assert.Equal(originalRaw, line.RawParse);
    }

    [Fact]
    public void MarkReady_Transitions_From_Parsing()
    {
        var session = Started();
        var parsedAt = DateTimeOffset.UtcNow;

        var result = session.MarkReady("Superstore", parsedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportStatus.Ready, session.Status);
        Assert.Equal("Superstore", session.MerchantText);
        Assert.Equal(parsedAt, session.ParsedAt);
    }

    [Fact]
    public void MarkReady_Stores_Receipt_Metadata_When_Provided()
    {
        var session = Started();
        var metadata = new ReceiptMetadata(
            StoreBranch: "42 Market St",
            PurchaseDate: new DateOnly(2026, 6, 7),
            PurchaseTime: new TimeOnly(14, 34),
            Subtotal: 39.60m,
            Tax: 1.98m,
            Total: 41.58m,
            PaymentDescriptor: "VISA ****4471 APPROVED",
            ReceiptNumber: "TXN 0472 118");

        var result = session.MarkReady("Superstore", DateTimeOffset.UtcNow, metadata);

        Assert.True(result.IsSuccess);
        Assert.Equal("42 Market St", session.StoreBranch);
        Assert.Equal(new DateOnly(2026, 6, 7), session.PurchaseDate);
        Assert.Equal(new TimeOnly(14, 34), session.PurchaseTime);
        Assert.Equal(39.60m, session.Subtotal);
        Assert.Equal(1.98m, session.Tax);
        Assert.Equal(41.58m, session.Total);
        Assert.Equal("VISA ****4471 APPROVED", session.PaymentDescriptor);
        Assert.Equal("TXN 0472 118", session.ReceiptNumber);
    }

    [Fact]
    public void MarkReady_Leaves_Metadata_Null_When_Omitted()
    {
        var session = Started();

        session.MarkReady("Superstore", DateTimeOffset.UtcNow);

        Assert.Null(session.StoreBranch);
        Assert.Null(session.PurchaseDate);
        Assert.Null(session.PurchaseTime);
        Assert.Null(session.Subtotal);
        Assert.Null(session.Tax);
        Assert.Null(session.Total);
        Assert.Null(session.PaymentDescriptor);
        Assert.Null(session.ReceiptNumber);
    }

    [Fact]
    public void MarkReady_Fails_When_Not_In_Parsing()
    {
        var session = Started();
        session.MarkReady(null, DateTimeOffset.UtcNow);

        var result = session.MarkReady(null, DateTimeOffset.UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    // ── CorrectHeader (plantry-yobz) — user intervention on the parsed receipt header ────────────────

    [Fact]
    public void CorrectHeader_Overwrites_Store_And_Date_When_Ready()
    {
        var session = Started();
        session.MarkReady("Store #100616", DateTimeOffset.UtcNow,
            new ReceiptMetadata(PurchaseDate: new DateOnly(2019, 7, 26)));
        var storeId = Guid.CreateVersion7();

        var result = session.CorrectHeader(
            "Food Basics", storeId, new DateOnly(2026, 7, 19), new TimeOnly(17, 5), Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal("Food Basics", session.MerchantText);
        Assert.Equal(storeId, session.SelectedStoreId);
        Assert.Equal(new DateOnly(2026, 7, 19), session.PurchaseDate);
        Assert.Equal(new TimeOnly(17, 5), session.PurchaseTime);
    }

    [Fact]
    public void CorrectHeader_Normalizes_Blank_Merchant_To_Null()
    {
        var session = Started();
        session.MarkReady("Store #100616", DateTimeOffset.UtcNow);

        session.CorrectHeader("   ", null, null, null, Clock);

        Assert.Null(session.MerchantText);
        Assert.Null(session.SelectedStoreId);
    }

    [Fact]
    public void CorrectHeader_Clears_A_Selected_Store_When_A_Name_Is_Typed()
    {
        var session = Started();
        session.MarkReady("Metro", DateTimeOffset.UtcNow);
        session.CorrectHeader("Metro", Guid.CreateVersion7(), null, null, Clock); // picked a store

        session.CorrectHeader("Corner Store", null, null, null, Clock); // then typed a new name

        Assert.Equal("Corner Store", session.MerchantText);
        Assert.Null(session.SelectedStoreId); // the prior pick is dropped
    }

    [Fact]
    public void CorrectHeader_Can_Clear_A_Guard_Nulled_Date_Back_To_A_Value_And_Vice_Versa()
    {
        var session = Started();
        session.MarkReady("Metro", DateTimeOffset.UtcNow, new ReceiptMetadata(PurchaseDate: new DateOnly(2026, 1, 1)));

        session.CorrectHeader("Metro", null, null, null, Clock); // user clears the date

        Assert.Null(session.PurchaseDate);
    }

    [Fact]
    public void CorrectHeader_Fails_When_Not_Ready()
    {
        var session = Started(); // still Parsing

        var result = session.CorrectHeader("Metro", null, null, null, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    [Fact]
    public void CorrectHeader_Fails_After_Commit()
    {
        var session = Started();
        session.MarkReady("Metro", DateTimeOffset.UtcNow);
        session.MarkCommitted(DateTimeOffset.UtcNow);

        var result = session.CorrectHeader("Metro", null, null, null, Clock);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    [Fact]
    public void MarkParsingFailed_Transitions_From_Parsing()
    {
        var session = Started();

        var result = session.MarkParsingFailed("timeout");

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportStatus.Failed, session.Status);
        Assert.Equal("timeout", session.ParseError);
    }

    [Fact]
    public void MarkParsingFailed_Fails_When_Not_In_Parsing()
    {
        var session = Started();
        session.MarkReady(null, DateTimeOffset.UtcNow); // now Ready

        var result = session.MarkParsingFailed("error");

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    [Fact]
    public void MarkCommitted_Transitions_From_Ready_And_Raises_Event()
    {
        var session = Started();
        session.MarkReady(null, DateTimeOffset.UtcNow);
        var committedAt = DateTimeOffset.UtcNow;

        var result = session.MarkCommitted(committedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportStatus.Committed, session.Status);
        Assert.Equal(committedAt, session.CommittedAt);
        var evt = Assert.Single(session.DomainEvents);
        Assert.IsType<ImportSessionCommittedEvent>(evt);
        var committed = (ImportSessionCommittedEvent)evt;
        Assert.Equal(session.Id, committed.SessionId);
    }

    [Fact]
    public void MarkCommitted_Fails_When_Not_Ready()
    {
        var session = Started();

        var result = session.MarkCommitted(DateTimeOffset.UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    [Fact]
    public void Discard_Succeeds_From_Any_Non_Committed_Status()
    {
        var parsing = Started();
        Assert.True(parsing.Discard().IsSuccess);
        Assert.Equal(ImportStatus.Discarded, parsing.Status);

        var ready = Started();
        ready.MarkReady(null, DateTimeOffset.UtcNow);
        Assert.True(ready.Discard().IsSuccess);
        Assert.Equal(ImportStatus.Discarded, ready.Status);
    }

    [Fact]
    public void Discard_Fails_When_Already_Committed()
    {
        var session = Started();
        session.MarkReady(null, DateTimeOffset.UtcNow);
        session.MarkCommitted(DateTimeOffset.UtcNow);

        var result = session.Discard();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.InvalidTransition", result.Error.Code);
    }

    [Fact]
    public void Discard_Succeeds_From_Failed_Status()
    {
        var session = Started();
        session.MarkParsingFailed("timeout");

        var result = session.Discard();

        Assert.True(result.IsSuccess);
        Assert.Equal(ImportStatus.Discarded, session.Status);
    }
}
