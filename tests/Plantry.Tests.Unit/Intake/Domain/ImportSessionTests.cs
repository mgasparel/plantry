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
    public void MarkReady_Fails_When_Not_In_Parsing()
    {
        var session = Started();
        session.MarkReady(null, DateTimeOffset.UtcNow);

        var result = session.MarkReady(null, DateTimeOffset.UtcNow);

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
}
