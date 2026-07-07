using Microsoft.Extensions.Logging.Abstractions;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for <see cref="IngestFlyer"/> (P5-6 / DJ2) over faked <c>IFlyerSource</c>/<c>IDealMatcher</c>:
/// remembered → auto-confirm via P5-5 (deal_observation, reviewer null); unremembered → Pending with the
/// AI proposal; byte-identical re-pull → no-op; changed re-pull → refresh only Pending, freeze resolved;
/// a parse failure → import Failed with error_detail + no partial deals + cycle continues;
/// <c>FlyerImported(pendingCount)</c> emitted on parse.
/// </summary>
public sealed class IngestFlyerTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid StoreId = Guid.NewGuid();
    private const string ExternalRef = "flipp-metro";

    private static ValidityWindow Window() =>
        ValidityWindow.Create(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7)).Value;

    private static RawDeal Raw(string name, decimal price = 4.99m) =>
        new(name, Brand: null, Size: null, price, Quantity: 1m, UnitId: null, SaleStory: null, Window());

    private static FlyerPullResult Pull(string externalId, string content, params RawDeal[] deals) =>
        new(externalId, Window(), content, deals);

    private sealed class Harness
    {
        public FakeStoreSubscriptionRepository Subs { get; } = new();
        public FakeFlyerImportRepository Imports { get; } = new();
        public FakeDealRepository Deals { get; } = new();
        public FakeDealMatchMemoryRepository Memories { get; } = new();
        public FakeIngestFlyerSource Source { get; } = new();
        public FakeDealMatcher Matcher { get; } = new();
        public FakeCatalogStoreReader Stores { get; } = new();
        public FakeCatalogProductReader Products { get; } = new();
        public FakePriceObservationWriter Observations { get; } = new();
        public TestClock Clock { get; } = new();
        public FakeTenantContext Tenant { get; } = new(Household.Value);

        public IngestFlyer Build()
        {
            var confirm = new ConfirmDeal(Deals, Memories, Products, Observations, Clock, Tenant, NullLogger<ConfirmDeal>.Instance);
            return new IngestFlyer(
                Subs, Imports, Deals, Memories, Source, Matcher, Stores, Products, confirm,
                Tenant, Clock, NullLogger<IngestFlyer>.Instance);
        }

        public StoreSubscription Subscribe(Guid storeId, string externalRef, string name = "Metro")
        {
            var sub = StoreSubscription.Subscribe(Household, storeId, "M5V0A1", Clock);
            Subs.Items.Add(sub);
            Stores.Names[storeId] = name;
            Stores.ExternalRefs[storeId] = externalRef;
            return sub;
        }
    }

    [Fact(DisplayName = "Remembered match auto-confirms via P5-5 (observation written, reviewer null); unremembered lands Pending with the proposal")]
    public async Task Materializes_Remembered_And_Pending()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        var rememberedProduct = Guid.NewGuid();

        // Positive memory for "milk" → auto-confirm; "eggs" has an AI proposal but no memory → Pending.
        var milkKey = DealNormalizer.Normalize("Milk 2L");
        h.Memories.Items.Add(DealMatchMemory.Remember(Household, StoreId, milkKey, "Milk 2L", rememberedProduct, by: null, h.Clock));
        var eggsProduct = Guid.NewGuid();
        h.Products.Candidates.Add(new ProductCandidate(eggsProduct, "Eggs"));
        h.Matcher.ByRawName["Eggs Dozen"] = new MatchProposal(eggsProduct, MatchConfidence.Low, "looks like eggs");

        h.Source.EnqueuePull(ExternalRef, Pull("flyer-100", "{v:1}", Raw("Milk 2L"), Raw("Eggs Dozen")));

        var summary = await h.Build().RunAsync();

        Assert.Equal(1, summary.Pulled);
        Assert.Equal(1, summary.AutoConfirmed);
        Assert.Equal(1, summary.PendingCreated);

        var milk = h.Deals.Items.Single(d => d.RawName == "Milk 2L");
        Assert.Equal(DealStatus.Confirmed, milk.Status);
        Assert.True(milk.AutoMatched);
        Assert.Equal(rememberedProduct, milk.ProductId);

        var eggs = h.Deals.Items.Single(d => d.RawName == "Eggs Dozen");
        Assert.Equal(DealStatus.Pending, eggs.Status);
        Assert.Equal(eggsProduct, eggs.SuggestedProductId);
        Assert.Equal(MatchConfidence.Low, eggs.MatchConfidence);

        // P5-5 side effect: a deal-sourced observation with a null reviewer (memory auto-confirm).
        var obs = Assert.Single(h.Observations.Observations);
        Assert.Equal(rememberedProduct, obs.ProductId);
        Assert.Null(obs.ReviewedByUserId);

        // FlyerImported emitted on parse with the point-in-time pending count.
        var import = Assert.Single(h.Imports.Items);
        Assert.Equal(PullStatus.Parsed, import.Status);
        var evt = Assert.IsType<FlyerImportedEvent>(import.DomainEvents.Single(e => e is FlyerImportedEvent));
        Assert.Equal(1, evt.PendingCount);
    }

    [Fact(DisplayName = "An AI suggestion outside the candidate set is dropped, not invented (ADR-007)")]
    public async Task Drops_Invented_Match()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        h.Products.Candidates.Add(new ProductCandidate(Guid.NewGuid(), "Real Product"));
        h.Matcher.ByRawName["Mystery Item"] = new MatchProposal(Guid.NewGuid(), MatchConfidence.High, "hallucinated");

        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{}", Raw("Mystery Item")));
        await h.Build().RunAsync();

        var deal = Assert.Single(h.Deals.Items);
        Assert.Null(deal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, deal.MatchConfidence);
        Assert.Equal(DealStatus.Pending, deal.Status);
    }

    [Fact(DisplayName = "Byte-identical re-pull is a no-op: same FlyerImport, no duplicate deals (DD5)")]
    public async Task Repull_Identical_IsNoOp()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{same}", Raw("Bread")));
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{same}", Raw("Bread")));

        await h.Build().RunAsync();
        var dealsAfterFirst = h.Deals.Items.Count;
        var second = await h.Build().RunAsync();

        Assert.Single(h.Imports.Items);            // no duplicate import
        Assert.Equal(1, dealsAfterFirst);
        Assert.Single(h.Deals.Items);              // no duplicate deal
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Pulled);
    }

    [Fact(DisplayName = "Re-pull with a volatile raw payload but unchanged deals is a no-op — the DD5 hash is over the deal projection, not raw bytes (plantry-04ji.4)")]
    public async Task Repull_VolatileRawContent_SameDeals_IsNoOp()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);

        // Same advertised deals across both pulls, but the raw items payload differs (Flipp impression
        // counters / generated timestamps) AND the items are reordered. Neither is a meaningful change, so
        // the canonical projection hashes identically and the second pull must be a DD5 no-op — no re-stage,
        // no wasted AI-matcher pass over an unchanged flyer.
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{\"impressions\":1}", Raw("Bread", 2.49m), Raw("Milk", 3.99m)));
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{\"impressions\":42}", Raw("Milk", 3.99m), Raw("Bread", 2.49m)));

        await h.Build().RunAsync();
        var dealsAfterFirst = h.Deals.Items.Count;
        var second = await h.Build().RunAsync();

        Assert.Single(h.Imports.Items);      // no duplicate import
        Assert.Equal(2, dealsAfterFirst);
        Assert.Equal(2, h.Deals.Items.Count); // no duplicate / re-staged deals
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Pulled);
    }

    [Fact(DisplayName = "Changed re-pull updates the same import, refreshes only Pending, freezes Confirmed/Rejected (DD13)")]
    public async Task Repull_Changed_RefreshesOnlyPending()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        var rememberedProduct = Guid.NewGuid();
        h.Memories.Items.Add(DealMatchMemory.Remember(
            Household, StoreId, DealNormalizer.Normalize("Cheese"), "Cheese", rememberedProduct, by: null, h.Clock));

        // First pull: Cheese (auto-confirmed) + Yogurt (pending).
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{v1}", Raw("Cheese", 5.00m), Raw("Yogurt", 3.00m)));
        // Changed re-pull: Cheese again (frozen) + Yogurt at a new price (refreshed).
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{v2}", Raw("Cheese", 9.99m), Raw("Yogurt", 2.50m)));

        await h.Build().RunAsync();
        var cheeseIdBefore = h.Deals.Items.Single(d => d.RawName == "Cheese").Id;
        await h.Build().RunAsync();

        Assert.Single(h.Imports.Items); // same import, updated

        // Cheese resolved → frozen: same deal, original price, still Confirmed (not re-priced in v1, DD13).
        var cheese = Assert.Single(h.Deals.Items, d => d.RawName == "Cheese");
        Assert.Equal(cheeseIdBefore, cheese.Id);
        Assert.Equal(DealStatus.Confirmed, cheese.Status);
        Assert.Equal(5.00m, cheese.Price);

        // Yogurt was Pending → refreshed to the new price.
        var yogurt = Assert.Single(h.Deals.Items, d => d.RawName == "Yogurt");
        Assert.Equal(DealStatus.Pending, yogurt.Status);
        Assert.Equal(2.50m, yogurt.Price);
    }

    [Fact(DisplayName = "A parse failure marks the import Failed with error_detail, writes NO partial deals, and the cycle continues")]
    public async Task ParseFailure_MarksFailed_NoPartialDeals_ContinuesCycle()
    {
        var h = new Harness();
        var badStore = Guid.NewGuid();
        var goodStore = Guid.NewGuid();
        h.Subscribe(badStore, "flipp-bad");
        h.Subscribe(goodStore, "flipp-good");

        // Bad flyer: a blank item name makes Deal.Stage throw mid-materialize (a parse failure) after a
        // valid envelope was pulled — so the import exists and is marked Failed with no deals persisted.
        h.Source.EnqueuePull("flipp-bad", Pull("flyer-bad", "{}", Raw("Real Item"), Raw("   ")));
        h.Source.EnqueuePull("flipp-good", Pull("flyer-good", "{}", Raw("Apples")));

        var summary = await h.Build().RunAsync();

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Pulled); // the good subscription still processed

        var failed = h.Imports.Items.Single(i => i.FlyerExternalId == "flyer-bad");
        Assert.Equal(PullStatus.Failed, failed.Status);
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorDetail));

        // No partial deals from the failed flyer; the good flyer's deal exists.
        Assert.DoesNotContain(h.Deals.Items, d => d.FlyerImportId == failed.Id);
        Assert.Single(h.Deals.Items, d => d.RawName == "Apples");
    }

    [Fact(DisplayName = "A malformed item on a CHANGED re-pull preserves the prior Pending deal and never blocks the next subscription")]
    public async Task Repull_ParseFailure_PreservesPriorPending_AndContinues()
    {
        var h = new Harness();
        var badStore = Guid.NewGuid();
        var goodStore = Guid.NewGuid();
        h.Subscribe(badStore, "flipp-bad");   // processed first (subscribed first)
        h.Subscribe(goodStore, "flipp-good");

        // Cycle 1: both stores parse cleanly, each leaving one Pending deal.
        h.Source.EnqueuePull("flipp-bad", Pull("flyer-bad", "{v1}", Raw("Yogurt", 3.00m)));
        h.Source.EnqueuePull("flipp-good", Pull("flyer-good", "{g1}", Raw("Apples", 2.00m)));
        await h.Build().RunAsync();

        // Cycle 2: bad store's changed re-pull carries a malformed (blank) item → Deal.Stage throws mid-stage;
        // good store's changed re-pull is clean and must still commit despite the bad store failing first.
        h.Source.EnqueuePull("flipp-bad", Pull("flyer-bad", "{v2}", Raw("Yogurt", 9.99m), Raw("   ")));
        h.Source.EnqueuePull("flipp-good", Pull("flyer-good", "{g2}", Raw("Apples", 1.50m)));
        var summary = await h.Build().RunAsync();

        Assert.Equal(1, summary.Failed);

        // The bad store's prior Pending deal survived at its ORIGINAL price — the failed refresh never
        // mutated the shared context (no partial delete leaked to the good store's save).
        var yogurt = Assert.Single(h.Deals.Items, d => d.RawName == "Yogurt");
        Assert.Equal(DealStatus.Pending, yogurt.Status);
        Assert.Equal(3.00m, yogurt.Price);

        // The good store still committed its refresh.
        var apples = Assert.Single(h.Deals.Items, d => d.RawName == "Apples");
        Assert.Equal(1.50m, apples.Price);
    }

    [Fact(DisplayName = "A pull that returns no envelope (Flipp unreachable) is skipped and the cycle continues")]
    public async Task PullFailure_NoEnvelope_Skips()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        h.Source.EnqueuePull(ExternalRef, FlyerPullResult.Failed("Flipp unreachable"));

        var summary = await h.Build().RunAsync();

        Assert.Empty(h.Imports.Items);   // no persistable envelope → no import row
        Assert.Empty(h.Deals.Items);
        Assert.Equal(0, summary.Pulled);
        Assert.Equal(0, summary.Failed); // not a hard subscription failure
    }

    [Fact(DisplayName = "A subscription whose store has no external ref is skipped (nothing to pull)")]
    public async Task NoExternalRef_Skips()
    {
        var h = new Harness();
        var sub = StoreSubscription.Subscribe(Household, StoreId, "M5V0A1", h.Clock);
        h.Subs.Items.Add(sub);
        h.Stores.Names[StoreId] = "Store With No Ref";
        h.Stores.ExternalRefs[StoreId] = null;

        var summary = await h.Build().RunAsync();

        Assert.Equal(1, summary.Skipped);
        Assert.Empty(h.Source.PullCalls);
    }

    [Fact(DisplayName = "With no armed tenant the cycle is an empty no-op — never a cross-tenant read")]
    public async Task NoTenant_IsEmptyNoOp()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-1", "{}", Raw("Milk")));

        var confirm = new ConfirmDeal(h.Deals, h.Memories, h.Products, h.Observations, h.Clock,
            new FakeTenantContext(null), NullLogger<ConfirmDeal>.Instance);
        var ingest = new IngestFlyer(
            h.Subs, h.Imports, h.Deals, h.Memories, h.Source, h.Matcher, h.Stores, h.Products, confirm,
            new FakeTenantContext(null), h.Clock, NullLogger<IngestFlyer>.Instance);

        var summary = await ingest.RunAsync();

        Assert.Equal(0, summary.Processed);
        Assert.Empty(h.Imports.Items);
        Assert.Empty(h.Source.PullCalls);
    }

    [Fact(DisplayName = "The AI matcher is called ONCE per flyer with all items, not once per item (plantry-04ji)")]
    public async Task Matcher_Is_Batched_Once_Per_Flyer()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        // A non-empty catalog: the empty-catalog guard (plantry-04ji.2) skips the matcher when there are no
        // candidates, so one candidate must exist for the "matcher IS batched" path to run.
        h.Products.Candidates.Add(new ProductCandidate(Guid.NewGuid(), "Some Product"));

        var deals = Enumerable.Range(0, 30).Select(i => Raw($"Item {i}")).ToArray();
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-batch", "{v:1}", deals));

        await h.Build().RunAsync();

        // One batch call from the application's view, carrying every item (the adapter chunks internally).
        Assert.Equal(1, h.Matcher.BatchCalls);
        Assert.Equal(30, h.Matcher.Calls.Count);
        Assert.Equal(deals.Select(d => d.RawName), h.Matcher.Calls);
    }

    [Fact(DisplayName = "Memory hits are resolved BEFORE batching and never sent to the AI matcher (DD3)")]
    public async Task Memory_Hits_Are_Excluded_From_The_Batch()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);

        // A non-empty catalog so the memory-miss item actually reaches the matcher (the empty-catalog guard,
        // plantry-04ji.2, would otherwise skip the matcher regardless of the memory split).
        h.Products.Candidates.Add(new ProductCandidate(Guid.NewGuid(), "Some Product"));

        // Positive + negative memory both short-circuit the AI; only the unremembered item is matched.
        var remembered = Guid.NewGuid();
        var posKey = DealNormalizer.Normalize("Known Milk");
        var negKey = DealNormalizer.Normalize("Known Junk");
        h.Memories.Items.Add(DealMatchMemory.Remember(Household, StoreId, posKey, "Known Milk", remembered, by: null, h.Clock));
        h.Memories.Items.Add(DealMatchMemory.RememberNegative(Household, StoreId, negKey, "Known Junk", by: null, h.Clock));

        h.Source.EnqueuePull(ExternalRef, Pull("flyer-mem", "{v:1}",
            Raw("Known Milk"), Raw("Fresh Item"), Raw("Known Junk")));

        await h.Build().RunAsync();

        // Exactly one memory-miss reached the matcher; the two remembered items did not.
        Assert.Equal(1, h.Matcher.BatchCalls);
        Assert.Equal(["Fresh Item"], h.Matcher.Calls);
    }

    [Fact(DisplayName = "A flyer of only memory hits issues no AI call at all")]
    public async Task All_Memory_Hits_Skip_The_Matcher_Entirely()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);

        var product = Guid.NewGuid();
        var key = DealNormalizer.Normalize("Only Item");
        h.Memories.Items.Add(DealMatchMemory.Remember(Household, StoreId, key, "Only Item", product, by: null, h.Clock));

        h.Source.EnqueuePull(ExternalRef, Pull("flyer-allmem", "{v:1}", Raw("Only Item")));

        await h.Build().RunAsync();

        Assert.Equal(0, h.Matcher.BatchCalls);
        Assert.Empty(h.Matcher.Calls);
    }

    [Fact(DisplayName = "An empty candidate catalog skips the AI matcher entirely; every item lands Pending/Unmatched (plantry-04ji.2)")]
    public async Task Empty_Candidate_Catalog_Skips_The_Matcher_Entirely()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);
        // No candidates added: an empty/new catalog. Every completion could only ever return 'none'.

        var deals = Enumerable.Range(0, 20).Select(i => Raw($"Item {i}")).ToArray();
        h.Source.EnqueuePull(ExternalRef, Pull("flyer-empty-catalog", "{v:1}", deals));

        var summary = await h.Build().RunAsync();

        // The guard: not a single AI call is issued despite 20 memory-miss items.
        Assert.Equal(0, h.Matcher.BatchCalls);
        Assert.Empty(h.Matcher.Calls);

        // Every deal still staged — Pending, with no suggested product.
        Assert.Equal(20, summary.PendingCreated);
        Assert.Equal(20, h.Deals.Items.Count);
        Assert.All(h.Deals.Items, d =>
        {
            Assert.Equal(DealStatus.Pending, d.Status);
            Assert.Null(d.SuggestedProductId);
            Assert.Equal(MatchConfidence.None, d.MatchConfidence);
        });
    }

    [Fact(DisplayName = "Batch proposals map back to the correct deal positionally")]
    public async Task Batch_Proposals_Align_Positionally()
    {
        var h = new Harness();
        h.Subscribe(StoreId, ExternalRef);

        var milk = Guid.NewGuid();
        var cheese = Guid.NewGuid();
        h.Products.Candidates.Add(new ProductCandidate(milk, "Milk"));
        h.Products.Candidates.Add(new ProductCandidate(cheese, "Cheese"));
        h.Matcher.ByRawName["Milk Item"] = new MatchProposal(milk, MatchConfidence.High, "milk");
        h.Matcher.ByRawName["Cheese Item"] = new MatchProposal(cheese, MatchConfidence.Low, "cheese");
        // "Plain Item" has no scripted proposal → Unmatched.

        h.Source.EnqueuePull(ExternalRef, Pull("flyer-align", "{v:1}",
            Raw("Milk Item"), Raw("Plain Item"), Raw("Cheese Item")));

        await h.Build().RunAsync();

        var milkDeal = h.Deals.Items.Single(d => d.RawName == "Milk Item");
        Assert.Equal(milk, milkDeal.SuggestedProductId);
        Assert.Equal(MatchConfidence.High, milkDeal.MatchConfidence);

        var plainDeal = h.Deals.Items.Single(d => d.RawName == "Plain Item");
        Assert.Null(plainDeal.SuggestedProductId);
        Assert.Equal(MatchConfidence.None, plainDeal.MatchConfidence);

        var cheeseDeal = h.Deals.Items.Single(d => d.RawName == "Cheese Item");
        Assert.Equal(cheese, cheeseDeal.SuggestedProductId);
        Assert.Equal(MatchConfidence.Low, cheeseDeal.MatchConfidence);
    }
}
