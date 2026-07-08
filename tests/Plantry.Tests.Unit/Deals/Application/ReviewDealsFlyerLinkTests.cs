using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for the review queue's flyer-link projection (q9zr.7): <see cref="ReviewDeals.ProjectPendingQueueAsync"/>
/// batch-resolves each flyer chapter's source <see cref="FlyerImport"/> by (store, validity-window) and stamps
/// <see cref="FlyerBlock.FlyerExternalId"/>. Proves the batch is a single read (no N+1, mirroring the store/product
/// name resolves), that a chapter with no Parsed import is left unlinked, and that the external id is carried through
/// for a future direct deep link.
/// </summary>
public sealed class ReviewDealsFlyerLinkTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid FreshCo = Guid.NewGuid();
    private static readonly Guid NoFrills = Guid.NewGuid();
    private static readonly Guid Metro = Guid.NewGuid();

    private readonly FakeDealRepository _deals = new();
    private readonly FakeCatalogProductReader _products = new();
    private readonly FakeCatalogStoreReader _stores = new();
    private readonly FakeFlyerImportRepository _flyerImports = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

    private ReviewDeals Sut => new(_deals, _products, _stores, _flyerImports, _clock);

    private static readonly DateOnly Today = new(2026, 7, 1);

    public ReviewDealsFlyerLinkTests()
    {
        _stores.Names[FreshCo] = "FreshCo";
        _stores.Names[NoFrills] = "No Frills";
        _stores.Names[Metro] = "Metro";
    }

    private void StagePending(Guid storeId, DateOnly from, DateOnly to)
    {
        var window = ValidityWindow.Create(from, to).Value;
        var raw = new RawDeal("SOME DEAL", null, null, 3.99m, null, null, "Save $1", window);
        var deal = Deal.Stage(
            Household, FlyerImportId.New(), storeId, raw, DealNormalizer.Normalize("SOME DEAL"),
            MatchProposal.Unmatched(), _clock);
        _deals.Items.Add(deal);
    }

    private void SeedParsedImport(Guid storeId, string externalId, DateOnly from, DateOnly to)
    {
        var window = ValidityWindow.Create(from, to).Value;
        var import = FlyerImport.Start(Household, storeId, externalId, contentHash: null, window, "{}", _clock);
        import.MarkParsed(pendingCount: 1, _clock);
        _flyerImports.Items.Add(import);
    }

    [Fact(DisplayName = "ProjectPendingQueueAsync stamps each flyer chapter's FlyerExternalId from its matching Parsed import")]
    public async Task Stamps_External_Id_On_Matching_Chapter()
    {
        StagePending(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        StagePending(NoFrills, Today.AddDays(-2), Today.AddDays(3));
        SeedParsedImport(FreshCo, "flipp-freshco-2026-07", Today.AddDays(-1), Today.AddDays(6));
        SeedParsedImport(NoFrills, "flipp-nofrills-2026-07", Today.AddDays(-2), Today.AddDays(3));

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Equal(
            "flipp-freshco-2026-07",
            projection.Flyers.Single(f => f.StoreId == FreshCo).FlyerExternalId);
        Assert.Equal(
            "flipp-nofrills-2026-07",
            projection.Flyers.Single(f => f.StoreId == NoFrills).FlyerExternalId);
    }

    [Fact(DisplayName = "The flyer imports are resolved in a single batch call over every chapter's store (no N+1)")]
    public async Task Resolves_In_A_Single_Batch_No_N_Plus_1()
    {
        StagePending(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        StagePending(NoFrills, Today.AddDays(-2), Today.AddDays(3));
        StagePending(Metro, Today.AddDays(-1), Today.AddDays(4));
        SeedParsedImport(FreshCo, "f", Today.AddDays(-1), Today.AddDays(6));

        await Sut.ProjectPendingQueueAsync();

        var call = Assert.Single(_flyerImports.ParsedRefsCalls);        // exactly one batch read, not one per flyer
        Assert.Equal(
            new[] { FreshCo, NoFrills, Metro }.OrderBy(g => g),
            call.OrderBy(g => g));                                       // and it carries every chapter's store
    }

    [Fact(DisplayName = "A chapter with no Parsed import (or only a mismatched window) is left with a null FlyerExternalId")]
    public async Task Leaves_Unlinked_When_No_Matching_Import()
    {
        StagePending(FreshCo, Today.AddDays(-1), Today.AddDays(6));   // no import at all
        StagePending(NoFrills, Today.AddDays(-2), Today.AddDays(3));
        // A Parsed import for the right store but a DIFFERENT window must not match this chapter.
        SeedParsedImport(NoFrills, "stale-window", Today.AddDays(-10), Today.AddDays(-3));

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Null(projection.Flyers.Single(f => f.StoreId == FreshCo).FlyerExternalId);
        Assert.Null(projection.Flyers.Single(f => f.StoreId == NoFrills).FlyerExternalId);
    }

    [Fact(DisplayName = "Only a Parsed import resolves — a Failed-only history leaves the chapter unlinked")]
    public async Task Ignores_Non_Parsed_Imports()
    {
        StagePending(FreshCo, Today.AddDays(-1), Today.AddDays(6));
        var window = ValidityWindow.Create(Today.AddDays(-1), Today.AddDays(6)).Value;
        var failed = FlyerImport.Start(Household, FreshCo, "flipp-freshco", contentHash: null, window, "{}", _clock);
        failed.MarkFailed("Flipp unreachable", _clock);   // never Parsed
        _flyerImports.Items.Add(failed);

        var projection = await Sut.ProjectPendingQueueAsync();

        Assert.Null(projection.Flyers.Single(f => f.StoreId == FreshCo).FlyerExternalId);
    }
}
