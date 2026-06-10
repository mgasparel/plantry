using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Unit.Intake.Application;

/// <summary>
/// L1 tests for <see cref="ParseSessionCommand"/> — the synchronous parse driver (SPEC §2a): a good
/// parse lands a Ready session with one line per parsed item and the raw AI payload quarantined on each
/// line (ACL provenance, ADR-007); a soft parse failure lands a Failed session carrying the message.
/// </summary>
public sealed class ParseSessionCommandTests
{
    private static readonly IClock Clock = SystemClock.Instance;
    private readonly Guid _household = Guid.NewGuid();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly byte[] _image = [0xFF, 0xD8, 0xFF];

    private ParseSessionCommand Parse(
        FakeImportSessionRepository repo, IReceiptParser parser, ICatalogHintProvider hints) =>
        new(_image, "image/jpeg", _userId, repo, parser, hints, Clock, new FakeTenantContext(_household));

    [Fact]
    public async Task Lands_A_Ready_Session_With_One_Line_Per_Parsed_Item()
    {
        var parser = new FakeReceiptParser(new ReceiptParseResult("Superstore",
        [
            new ParsedLine(1, "Flour 1kg", null, null, 1m, null, 4.99m, "high", """{"line":1}"""),
            new ParsedLine(2, "Milk 2L", "Milk", Guid.CreateVersion7(), 2m, null, 3.49m, "low", """{"line":2}"""),
        ]));
        var hints = new FakeCatalogHintProvider(new ProductHint(Guid.CreateVersion7(), "Flour", []));
        var repo = new FakeImportSessionRepository();

        var result = await Parse(repo, parser, hints).ExecuteAsync();

        Assert.True(result.IsSuccess);
        var session = Assert.Single(repo.Sessions);
        Assert.Equal(result.Value, session.Id);
        Assert.Equal(ImportStatus.Ready, session.Status);
        Assert.Equal("Superstore", session.MerchantText);
        Assert.Equal(2, session.Lines.Count);
        Assert.Equal(SuggestedConfidence.High, session.Lines.Single(l => l.LineNo == 1).SuggestedConfidence);
        Assert.Equal(SuggestedConfidence.Low, session.Lines.Single(l => l.LineNo == 2).SuggestedConfidence);

        // The receipt image is persisted as the 1:1 record of the attempt.
        Assert.Single(repo.Receipts);
        // The parser was handed the household's catalog hints.
        Assert.Single(parser.ReceivedHints!);
    }

    [Fact]
    public async Task Quarantines_The_Raw_AI_Payload_Verbatim_On_Each_Line()
    {
        const string rawOne = """{"line":1,"junk":"<script>"}""";
        var parser = new FakeReceiptParser(new ReceiptParseResult(null,
        [
            new ParsedLine(1, "Flour", null, null, null, null, null, "none", rawOne),
        ]));
        var repo = new FakeImportSessionRepository();

        await Parse(repo, parser, new FakeCatalogHintProvider()).ExecuteAsync();

        var line = Assert.Single(repo.Sessions.Single().Lines);
        Assert.Equal(rawOne, line.RawParse); // stored exactly as the AI returned it — never reinterpreted
    }

    [Fact]
    public async Task Marks_The_Session_Failed_On_A_Soft_Parse_Error()
    {
        var parser = new FakeReceiptParser(new ReceiptParseResult(null, [], "AI returned unparseable JSON"));
        var repo = new FakeImportSessionRepository();

        var result = await Parse(repo, parser, new FakeCatalogHintProvider()).ExecuteAsync();

        Assert.True(result.IsSuccess); // a parse failure is still a recorded session, not a command failure
        var session = Assert.Single(repo.Sessions);
        Assert.Equal(ImportStatus.Failed, session.Status);
        Assert.Equal("AI returned unparseable JSON", session.ParseError);
        Assert.Empty(session.Lines);
    }

    [Fact]
    public async Task Fails_When_No_Household_In_Context()
    {
        var repo = new FakeImportSessionRepository();
        var parser = new FakeReceiptParser(new ReceiptParseResult(null, []));

        var cmd = new ParseSessionCommand(
            _image, "image/jpeg", _userId, repo, parser, new FakeCatalogHintProvider(), Clock,
            new FakeTenantContext(null));
        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(repo.Sessions);
        Assert.Equal(0, parser.Calls); // never reaches the AI
    }

    [Fact]
    public async Task Fails_When_The_Image_Is_Empty()
    {
        var repo = new FakeImportSessionRepository();
        var parser = new FakeReceiptParser(new ReceiptParseResult(null, []));
        var cmd = new ParseSessionCommand(
            [], "image/jpeg", _userId, repo, parser, new FakeCatalogHintProvider(), Clock,
            new FakeTenantContext(_household));

        var result = await cmd.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Intake.EmptyImage", result.Error.Code);
        Assert.Equal(0, parser.Calls);
    }
}
