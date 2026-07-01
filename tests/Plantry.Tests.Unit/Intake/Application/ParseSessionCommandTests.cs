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
        var milkId = Guid.CreateVersion7();
        var parser = new FakeReceiptParser(new ReceiptParseResult("Superstore",
        [
            new ParsedLine(1, "Flour 1kg", null, null, 1m, null, 4.99m, "high", """{"line":1}"""),
            new ParsedLine(2, "Milk 2L", "Milk", milkId, 2m, "L", 3.49m, "low", """{"line":2}"""),
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

        // AI suggestion fields land on each line.
        var milkLine = session.Lines.Single(l => l.LineNo == 2);
        Assert.Equal(milkId, milkLine.SuggestedProductId);
        Assert.Equal("Milk", milkLine.SuggestedProductName);
        Assert.Equal(2m, milkLine.SuggestedQuantity);
        Assert.Equal("L", milkLine.SuggestedUnitLabel);
        Assert.Equal(3.49m, milkLine.SuggestedPrice);

        // The receipt image is persisted as the 1:1 record of the attempt.
        Assert.Single(repo.Receipts);
        // The parser was handed the household's catalog hints.
        Assert.Single(parser.ReceivedHints!);
    }

    [Fact]
    public async Task Lands_Receipt_Metadata_On_The_Ready_Session()
    {
        var metadata = new ReceiptMetadata(
            StoreBranch: "42 Market St",
            PurchaseDate: new DateOnly(2026, 6, 7),
            PurchaseTime: new TimeOnly(14, 34),
            Subtotal: 40.00m, Tax: 2.00m, Total: 42.00m,
            PaymentDescriptor: "VISA ****4471 APPROVED",
            ReceiptNumber: "TXN 0472 118");
        var parser = new FakeReceiptParser(new ReceiptParseResult("Superstore",
        [
            new ParsedLine(1, "Flour 1kg", null, null, 1m, null, 4.99m, "high", null),
        ], Metadata: metadata));
        var repo = new FakeImportSessionRepository();

        await Parse(repo, parser, new FakeCatalogHintProvider()).ExecuteAsync();

        var session = Assert.Single(repo.Sessions);
        Assert.Equal("42 Market St", session.StoreBranch);
        Assert.Equal(new DateOnly(2026, 6, 7), session.PurchaseDate);
        Assert.Equal(new TimeOnly(14, 34), session.PurchaseTime);
        Assert.Equal(42.00m, session.Total);
        Assert.Equal("VISA ****4471 APPROVED", session.PaymentDescriptor);
        Assert.Equal("TXN 0472 118", session.ReceiptNumber);
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

    [Fact]
    public async Task Alternatives_With_Two_Or_More_Candidates_Land_On_The_Line()
    {
        var productA = Guid.CreateVersion7();
        var productB = Guid.CreateVersion7();
        var parser = new FakeReceiptParser(new ReceiptParseResult("Test Mart",
        [
            new ParsedLine(1, "CHEDDAR BLK 400G", "Cheddar, Mild", productA, 1m, null, 4.99m, "high", null,
                Alternatives:
                [
                    new ParsedAlternative(productA, "Cheddar, Mild",  0.88m),
                    new ParsedAlternative(productB, "Cheddar, Sharp", 0.62m),
                ]),
        ]));
        var repo = new FakeImportSessionRepository();

        await Parse(repo, parser, new FakeCatalogHintProvider()).ExecuteAsync();

        var line = Assert.Single(repo.Sessions.Single().Lines);
        Assert.NotNull(line.SuggestedAlternatives);
        Assert.Equal(2, line.SuggestedAlternatives!.Count);
        Assert.Equal("Cheddar, Mild", line.SuggestedAlternatives[0].ProductName);
        Assert.Equal(0.88m, line.SuggestedAlternatives[0].Confidence);
        Assert.Equal(productA, line.SuggestedAlternatives[0].ProductId);
    }

    [Fact]
    public async Task Single_Alternative_Does_Not_Land_On_The_Line()
    {
        var productA = Guid.CreateVersion7();
        var parser = new FakeReceiptParser(new ReceiptParseResult("Test Mart",
        [
            new ParsedLine(1, "MILK 2L", "Milk", productA, 2m, "L", 3.49m, "high", null,
                Alternatives: [new ParsedAlternative(productA, "Milk", 0.95m)]),
        ]));
        var repo = new FakeImportSessionRepository();

        await Parse(repo, parser, new FakeCatalogHintProvider()).ExecuteAsync();

        var line = Assert.Single(repo.Sessions.Single().Lines);
        // Single-candidate alternatives are treated as no alternatives — block must not render.
        Assert.Null(line.SuggestedAlternatives);
    }
}
