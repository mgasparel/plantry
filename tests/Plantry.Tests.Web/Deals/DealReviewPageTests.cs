using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Catalog.Domain;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using CatalogUnit = Plantry.Catalog.Domain.Unit;
using SystemClock = Plantry.SharedKernel.Domain.SystemClock;

namespace Plantry.Tests.Web.Deals;

/// <summary>
/// L4/L5 tests for the deal review queue (P5-8 / DJ4). Uses the WAF harness with in-memory fakes for the
/// Deals repository, Catalog product/store ports, the match-memory repo, the price-observation writer, and
/// the Catalog reference/create repos — no Postgres touched. The real <c>ReviewDeals</c>,
/// <c>ConfirmDeal</c>, and <c>RejectDeal</c> run over the fakes, so these tests prove the verb wiring
/// (Confirm/Correct/Reject → the P5-5 commands, including inline-create) end to end, plus the
/// confidence-shaped render and the single-suggestion chip.
/// </summary>
public sealed class DealReviewPageTests(DealReviewFactory factory) : IClassFixture<DealReviewFactory>
{
    private static readonly Guid HouseholdId = Guid.Parse("dddddddd-0000-0000-0000-0000000000f8");

    private HttpClient AuthedClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdId.ToString());
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token found on the review page.");
        return m.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, params KeyValuePair<string, string>[] fields) =>
        client.PostAsync(url, new FormUrlEncodedContent(fields));

    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    // ── L4 render ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /Deals/Review with no pending deals renders the caught-up empty state")]
    public async Task Empty_Queue_Renders_Caught_Up()
    {
        factory.Reset();
        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        Assert.Contains("All caught up", html);
    }

    [Fact(DisplayName = "GET /Deals/Review renders each confidence treatment + the single-suggestion chip")]
    public async Task Renders_Confidence_Treatments()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        // Raw flyer names render verbatim (ACL quarantine).
        Assert.Contains("Milk 2L", html);
        Assert.Contains("Mystery Item", html);
        // _ConfidenceBadge treatment (High/Low/None) reused from Intake.
        Assert.Contains("Matched", html);      // High
        Assert.Contains("Check match", html);  // Low
        Assert.Contains("No match", html);     // None
        // Single-suggestion "did you mean" chip for a matched deal.
        Assert.Contains("Did you mean", html);
        Assert.Contains("Whole Milk", html);   // resolved suggestion name
        // The per-card "Unrecognized — no catalog match…" boilerplate is gone (q9zr.2 item 2): the badge +
        // verbs already communicate it, so it no longer repeats once per no-match row.
        Assert.DoesNotContain("Unrecognized", html);
        Assert.DoesNotContain("no catalog match", html);
        // Verbs present.
        Assert.Contains("Confirm", html);
        Assert.Contains("Correct", html);
        Assert.Contains("Reject", html);
    }

    [Fact(DisplayName = "GET /Deals/Review groups the queue into High → Low → None tier sections with counts")]
    public async Task Groups_Into_Confidence_Tier_Sections()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("Low A", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        factory.SeedPending("None B", MatchConfidence.None, suggested: null);
        factory.SeedPending("None C", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // The three tier section headers render in fixed High → Low → None order.
        var high = html.IndexOf("Looks right", StringComparison.Ordinal);
        var low = html.IndexOf("Needs a look", StringComparison.Ordinal);
        var none = html.IndexOf("Not in your catalog", StringComparison.Ordinal);
        Assert.True(high >= 0 && low >= 0 && none >= 0, "All three tier sections should render.");
        Assert.True(high < low && low < none, "Tier sections must be ordered High → Low → None.");

        // Each section header carries its own count (High 2 / Low 1 / None 3) immediately after the title.
        Assert.Matches(@"Looks right</span>\s*<span class=""ch-sub"">·\s*2", html);
        Assert.Matches(@"Needs a look</span>\s*<span class=""ch-sub"">·\s*1", html);
        Assert.Matches(@"Not in your catalog</span>\s*<span class=""ch-sub"">·\s*3", html);
    }

    [Fact(DisplayName = "GET /Deals/Review?dealId=<confirmed> renders the single auto-matched correction card")]
    public async Task Renders_Single_Correction_For_Confirmed_Deal()
    {
        factory.Reset();
        var deal = factory.SeedAutoConfirmed("Milk 2L", factory.MilkProduct);

        var html = await (await AuthedClient().GetAsync($"/Deals/Review?dealId={deal.Id.Value}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Currently matched to", html);
        Assert.Contains("Whole Milk", html);
        Assert.Contains("Correct", html);
        Assert.Contains("Reject", html);
    }

    [Fact(DisplayName = "Unauthenticated GET /Deals/Review returns 401")]
    public async Task Unauthenticated_Returns_401()
    {
        factory.Reset();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Deals/Review");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "GET /Deals/Review flags a $0.00 flyer-noise row and de-emphasises it")]
    public async Task Flags_Flyer_Noise_Rows()
    {
        factory.Reset();
        factory.SeedPending("AD MATCH", MatchConfidence.None, suggested: null, price: 0m);
        factory.SeedPending("Real Deal", MatchConfidence.None, suggested: null, price: 3.49m);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // The $0.00 row is flagged and gets the de-emphasis modifier; the priced row does not.
        Assert.Contains("Flyer noise", html);
        Assert.Contains("deal-review-row--noise", html);
        Assert.Single(Regex.Matches(html, "deal-review-row--noise"));
    }

    [Fact(DisplayName = "GET /Deals/Review title-cases raw names for display, keeping the verbatim string in the title attribute")]
    public async Task Title_Cases_Raw_Names_For_Display()
    {
        factory.Reset();
        factory.SeedPending("FRANK'S HOT SAUCE 375ML", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // Display is title-cased — capitalised after the start and spaces, but NOT after the apostrophe.
        Assert.Contains("Frank's Hot Sauce 375ml", html);
        Assert.DoesNotContain("Frank'S", html);   // no capital letter after the apostrophe
        // The verbatim ALL-CAPS flyer string is preserved untouched in the name's title attribute.
        Assert.Contains("title=\"FRANK'S HOT SAUCE 375ML\"", html);
    }

    // ── L4 correction sheet — deal context + prefilled search (q9zr.6) ─────────────

    [Fact(DisplayName = "GET /Deals/Review renders the correction sheet titled 'Match to a product' with the deal-context block and a selection-gated Add")]
    public async Task Correction_Sheet_Has_Match_Title_Deal_Context_And_Gated_Add()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.Low, factory.MilkProduct);

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        // (1) Search-view title is overridden for the Deals usage (Title param).
        Assert.Contains("Match to a product", html);
        // (2) Deal-context block renders, driven by the host's dealContext state (raw name + brand/price/store).
        Assert.Contains("sheet-deal-context", html);
        Assert.Contains("dealContext?.rawName", html);
        Assert.Contains("dealContext?.store", html);
        // (4) Add stays disabled until a product is picked (RequireSelection param).
        Assert.Contains(":disabled=\"!draft.productId\"", html);
    }

    [Fact(DisplayName = "The queue exposes the DOM hooks the correction sheet reads for verbatim deal context")]
    public async Task Queue_Exposes_Deal_Context_Dom_Hooks_For_The_Sheet()
    {
        factory.Reset();
        factory.SeedPending("BREYERS CREAMERY STYLE ICE CREAM", MatchConfidence.Low, factory.MilkProduct);

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        // openCorrect() reads the VERBATIM raw name from the name cell's title attribute (ACL quarantine)…
        Assert.Contains("deal-review-row__name", html);
        Assert.Contains("title=\"BREYERS CREAMERY STYLE ICE CREAM\"", html);
        // …the brand from the brand span (SeedPending stamps a brand)…
        Assert.Contains("deal-review-row__brand", html);
        // …the price from the amount cell…
        Assert.Contains("deal-row__amount", html);
        // …and the store from the active flyer rail chip.
        Assert.Contains("flyer-chip is-active", html);
        Assert.Contains("<span class=\"store\">", html);
    }

    // ── L4 flyer rail + chapters (q9zr.3) ──────────────────────────────────────────

    [Fact(DisplayName = "GET /Deals/Review renders the big-chip flyer rail with store, count, days-left and the progress header")]
    public async Task Renders_Flyer_Rail_And_Progress_Header()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // The rail renders as a big chip (single flyer ≤ 3) carrying the store, a count·expiry pill, and the range.
        Assert.Contains("flyer-chip", html);
        Assert.Contains("FreshCo", html);       // store name lives in the rail
        Assert.Contains("d left", html);        // days-left badge
        // Overall progress header spans all flyers.
        Assert.Contains("reviewed", html);
        Assert.Contains("0 of 3 reviewed", html);
    }

    [Fact(DisplayName = "Store name + validity dates appear only in the rail, never repeated per card (q9zr.3 dedupe)")]
    public async Task Store_And_Dates_Only_In_Rail_Not_On_Cards()
    {
        factory.Reset();
        factory.SeedPending("Deal One", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Deal Two", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("Deal Three", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // Three cards in one flyer, but the validity range renders exactly once — in the rail chip, not per card.
        Assert.Single(Regex.Matches(html, @"Valid \w"));
        // The store name is not stamped on any card row's secondary line.
        Assert.DoesNotContain("catalog-list__secondary\">FreshCo", html);
    }

    [Fact(DisplayName = "GET /Deals/Review defaults to the soonest-expiring flyer and shows only its deals")]
    public async Task Defaults_To_Soonest_Flyer_And_Chapters_Its_Deals()
    {
        factory.Reset();
        factory.SeedPendingExpiring("Milk 2L", MatchConfidence.High, factory.MilkProduct, daysUntilExpiry: 2);
        factory.SeedPendingExpiring("Eggs Dozen", MatchConfidence.High, factory.BreadProduct, daysUntilExpiry: 6);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        // Two flyer chips on the rail; the soonest-expiring one (2 days) is active, so only its card shows.
        Assert.Contains("flyer-chip", html);
        Assert.Contains("Milk 2L", html);           // soonest flyer's deal is rendered
        Assert.DoesNotContain("Eggs Dozen", html);  // the other flyer is a chapter away, not a card here
    }

    [Fact(DisplayName = "Finishing a flyer's last deal hands off to the next flyer with a done interstitial")]
    public async Task Finishing_A_Flyer_Hands_Off_To_Next()
    {
        factory.Reset();
        var soon = factory.SeedPendingExpiring("Milk 2L", MatchConfidence.High, factory.MilkProduct, daysUntilExpiry: 2);
        var later = factory.SeedPendingExpiring("Eggs Dozen", MatchConfidence.High, factory.BreadProduct, daysUntilExpiry: 6);
        var soonKey = FlyerBlock.MakeKey(soon.StoreId, soon.ValidityWindow.ValidFrom, soon.ValidityWindow.ValidTo);
        var laterKey = FlyerBlock.MakeKey(later.StoreId, later.ValidityWindow.ValidFrom, later.ValidityWindow.ValidTo);

        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Reject the only deal in the soonest flyer, carrying the active flyer key — its last verb.
        var response = await PostAsync(client, $"/Deals/Review?handler=Reject&dealId={soon.Id.Value}&flyer={soonKey}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        var fragment = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // The done interstitial hands off to the next flyer — not its cards, and not "All caught up".
        Assert.Contains("Flyer cleared", fragment);
        Assert.Contains("Review FreshCo", fragment);        // CTA names the next flyer's store
        Assert.Contains($"flyer={laterKey}", fragment);     // and routes to it
        Assert.DoesNotContain("Milk 2L", fragment);         // the finished flyer's deal is gone
        Assert.DoesNotContain("Eggs Dozen", fragment);      // next flyer's cards are behind the interstitial
        Assert.DoesNotContain("All caught up", fragment);   // work remains, so not the empty state
    }

    [Fact(DisplayName = "Finishing the last flyer's last deal shows the caught-up empty state")]
    public async Task Finishing_The_Last_Flyer_Shows_Caught_Up()
    {
        factory.Reset();
        var only = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        var key = FlyerBlock.MakeKey(only.StoreId, only.ValidityWindow.ValidFrom, only.ValidityWindow.ValidTo);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, $"/Deals/Review?handler=Reject&dealId={only.Id.Value}&flyer={key}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        var fragment = await response.Content.ReadAsStringAsync();

        Assert.Contains("All caught up", fragment);
        Assert.DoesNotContain("Flyer cleared", fragment);
    }

    // ── L5 verb wiring ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Confirm accepts the suggestion → deal becomes Confirmed, writes an observation, leaves the queue")]
    public async Task Confirm_Accepts_Suggestion()
    {
        factory.Reset();
        var deal = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, $"/Deals/Review?handler=Confirm&dealId={deal.Id.Value}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(factory.MilkProduct, deal.ProductId);
        Assert.Equal(1, factory.Observations.Calls);          // deal-sourced observation written (confirm)
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Milk 2L", fragment);           // left the pending queue
    }

    [Fact(DisplayName = "Reject → deal becomes Rejected, writes NO observation, leaves the queue")]
    public async Task Reject_Writes_No_Observation()
    {
        factory.Reset();
        var deal = factory.SeedPending("Fresh Salmon", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, $"/Deals/Review?handler=Reject&dealId={deal.Id.Value}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Rejected, deal.Status);
        Assert.Null(deal.ProductId);
        Assert.Equal(0, factory.Observations.Calls);          // reject reaches Pricing never (D5)
        var fragment = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Fresh Salmon", fragment);
    }

    [Fact(DisplayName = "Correct with a different product → deal Confirmed to that product + memory repointed")]
    public async Task Correct_Resolves_Different_Product_And_Repoints_Memory()
    {
        factory.Reset();
        var deal = factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        // Pre-seed a positive memory for this deal's key pointing at the ORIGINAL (bread) product.
        var memory = factory.SeedMemory(deal, factory.BreadProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Correct to the milk product (a DIFFERENT catalog product) via the search-view pick.
        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("productId", factory.MilkProduct.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(factory.MilkProduct, deal.ProductId);    // resolved a different product
        Assert.Equal(factory.MilkProduct, memory.ProductId);  // memory rewritten (repointed)
        Assert.Equal(1, factory.Observations.Calls);          // supersede observation written
    }

    [Fact(DisplayName = "Correct via inline-create → mints a catalog product, then confirms the deal against it")]
    public async Task Correct_Inline_Create_Then_Confirm()
    {
        factory.Reset();
        var deal = factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("newProductName", "Artisan Mystery"),
            Kv("newProductUnitId", factory.UnitId.ToString()));

        response.EnsureSuccessStatusCode();
        // A catalog product was created …
        var created = Assert.Single(factory.Products.Items);
        Assert.Equal("Artisan Mystery", created.Name);
        // … and the deal was confirmed against the newly-created product.
        Assert.Equal(DealStatus.Confirmed, deal.Status);
        Assert.Equal(created.Id.Value, deal.ProductId);
        Assert.Equal(1, factory.Observations.Calls);
    }

    [Fact(DisplayName = "Correct an already-confirmed auto-matched deal → supersede (stays Confirmed, new product)")]
    public async Task Correct_Auto_Matched_Deal_Supersedes()
    {
        factory.Reset();
        var deal = factory.SeedAutoConfirmed("Milk 2L", factory.MilkProduct);
        Assert.True(deal.AutoMatched);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=Correct",
            Kv("__RequestVerificationToken", token),
            Kv("dealId", deal.Id.Value.ToString()),
            Kv("productId", factory.BreadProduct.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, deal.Status);      // still confirmed
        Assert.Equal(factory.BreadProduct, deal.ProductId);   // superseded to the new product
        Assert.False(deal.AutoMatched);                        // a manual correction clears the auto flag
        Assert.Equal(1, factory.Observations.Calls);          // append-only supersede observation
    }

    [Fact(DisplayName = "Search endpoint returns ranked <li> option markup for the correction sheet")]
    public async Task Search_Endpoint_Ranks_Candidates()
    {
        factory.Reset();
        var html = await (await AuthedClient().GetAsync("/Deals/Review?handler=SearchProducts&q=milk"))
            .Content.ReadAsStringAsync();
        Assert.Contains("<li", html);
        Assert.Contains("Whole Milk", html);
    }

    // ── L4/L5 bulk verbs (q9zr.4) ──────────────────────────────────────────────────

    [Fact(DisplayName = "Tier headers render Confirm all (N) on High and Dismiss all (N) on None with counts")]
    public async Task Bulk_Buttons_Render_With_Counts()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("None A", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(
            await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync());

        Assert.Contains("Confirm all (2)", html);
        Assert.Contains("Dismiss all (1)", html);
        // The hx-post URL must be a well-formed handler route, terminated by the closing quote or a flyer
        // query — never the un-substituted Razor literal "@flyerQs" (the email-heuristic trap that made the
        // live button post to a non-existent handler and fall through to a full-page render).
        Assert.Matches(@"hx-post=""/Deals/Review\?handler=ConfirmAll(&flyer=[^""]*)?""", html);
        Assert.Matches(@"hx-post=""/Deals/Review\?handler=DismissAll(&flyer=[^""]*)?""", html);
        Assert.DoesNotContain("@flyerQs", html);
    }

    [Fact(DisplayName = "ConfirmAll confirms every High deal via its OWN server-side SuggestedProductId, and toasts")]
    public async Task ConfirmAll_Confirms_High_Tier_Per_Deal_Suggestion()
    {
        factory.Reset();
        var milk = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        var bread = factory.SeedPending("Sourdough", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("Judgement Call", MatchConfidence.Low, factory.BreadProduct); // Low — untouched
        factory.SeedPending("Mystery", MatchConfidence.None, suggested: null);            // None — untouched
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=ConfirmAll",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        // Each High deal was confirmed to ITS OWN suggested product (a client injected nothing).
        Assert.Equal(DealStatus.Confirmed, milk.Status);
        Assert.Equal(factory.MilkProduct, milk.ProductId);
        Assert.Equal(DealStatus.Confirmed, bread.Status);
        Assert.Equal(factory.BreadProduct, bread.ProductId);
        Assert.Equal(2, factory.Observations.Calls);            // one deal-sourced observation per confirm
        // The Low and None deals were left alone (both still pending).
        Assert.Equal(2, factory.Repo.Items.Count(d => d.Status == DealStatus.Pending));
        // Plain status toast via HX-Trigger (no undo affordance).
        Assert.True(response.Headers.TryGetValues("HX-Trigger", out var trig));
        Assert.Contains("Confirmed 2 matches", string.Join("", trig!));
    }

    [Fact(DisplayName = "ConfirmAll honours a dealIds[] scope — confirms only the checked High deals, ignores foreign ids")]
    public async Task ConfirmAll_Scopes_To_Requested_Ids()
    {
        factory.Reset();
        var a = factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        var b = factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        var c = factory.SeedPending("High C", MatchConfidence.High, factory.MilkProduct);
        var none = factory.SeedPending("Mystery", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Post only A and C — plus a None id (outside the High eligible set) which must be ignored.
        var response = await PostAsync(client, "/Deals/Review?handler=ConfirmAll",
            Kv("__RequestVerificationToken", token),
            Kv("dealIds", a.Id.Value.ToString()),
            Kv("dealIds", c.Id.Value.ToString()),
            Kv("dealIds", none.Id.Value.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, a.Status);
        Assert.Equal(DealStatus.Confirmed, c.Status);
        Assert.Equal(DealStatus.Pending, b.Status);            // unchecked High stays pending
        Assert.Equal(DealStatus.Pending, none.Status);         // foreign id ignored, not rejected
        Assert.Equal(2, factory.Observations.Calls);
    }

    [Fact(DisplayName = "ConfirmAll a second time after re-render is an idempotent no-op (no extra observations, no toast)")]
    public async Task ConfirmAll_Is_Idempotent()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough", MatchConfidence.High, factory.BreadProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var first = await PostAsync(client, "/Deals/Review?handler=ConfirmAll",
            Kv("__RequestVerificationToken", token));
        first.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Observations.Calls);

        // Re-drive: the queue already re-rendered from truth, nothing is eligible — a clean no-op.
        var second = await PostAsync(client, "/Deals/Review?handler=ConfirmAll",
            Kv("__RequestVerificationToken", token));
        second.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Observations.Calls);           // no additional writes
        Assert.False(second.Headers.Contains("HX-Trigger"));   // nothing confirmed → no toast
    }

    [Fact(DisplayName = "DismissAll rejects every None deal — noise rows included — writes NO observation, and toasts")]
    public async Task DismissAll_Rejects_None_Tier_Including_Noise()
    {
        factory.Reset();
        var real = factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null, price: 3.49m);
        var noise = factory.SeedPending("AD MATCH", MatchConfidence.None, suggested: null, price: 0m); // noise, included
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);   // High — untouched
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=DismissAll",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Rejected, real.Status);
        Assert.Equal(DealStatus.Rejected, noise.Status);       // the $0.00 noise row is dismissed too
        Assert.Equal(0, factory.Observations.Calls);           // reject reaches Pricing never (D5)
        Assert.Equal(1, factory.Repo.Items.Count(d => d.Status == DealStatus.Pending)); // the High deal stands
        Assert.True(response.Headers.TryGetValues("HX-Trigger", out var trig));
        Assert.Contains("Dismissed 2 deals", string.Join("", trig!));
    }

    [Fact(DisplayName = "DismissAll honours a dealIds[] scope — dismisses only the requested None deals")]
    public async Task DismissAll_Scopes_To_Requested_Ids()
    {
        factory.Reset();
        var a = factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        var b = factory.SeedPending("None B", MatchConfidence.None, suggested: null);
        var c = factory.SeedPending("None C", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var response = await PostAsync(client, "/Deals/Review?handler=DismissAll",
            Kv("__RequestVerificationToken", token),
            Kv("dealIds", a.Id.Value.ToString()),
            Kv("dealIds", c.Id.Value.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Rejected, a.Status);
        Assert.Equal(DealStatus.Rejected, c.Status);
        Assert.Equal(DealStatus.Pending, b.Status);            // unrequested None stays pending
    }

    [Fact(DisplayName = "DismissAll a second time after re-render is an idempotent no-op")]
    public async Task DismissAll_Is_Idempotent()
    {
        factory.Reset();
        factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        factory.SeedPending("None B", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var first = await PostAsync(client, "/Deals/Review?handler=DismissAll",
            Kv("__RequestVerificationToken", token));
        first.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Repo.Items.Count(d => d.Status == DealStatus.Rejected));

        var second = await PostAsync(client, "/Deals/Review?handler=DismissAll",
            Kv("__RequestVerificationToken", token));
        second.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Repo.Items.Count(d => d.Status == DealStatus.Rejected)); // unchanged
        Assert.False(second.Headers.Contains("HX-Trigger"));
    }

    [Fact(DisplayName = "Unauthenticated bulk POSTs are rejected")]
    public async Task Bulk_Verbs_Require_Auth()
    {
        factory.Reset();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var confirm = await client.PostAsync("/Deals/Review?handler=ConfirmAll", new FormUrlEncodedContent([]));
        var dismiss = await client.PostAsync("/Deals/Review?handler=DismissAll", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Unauthorized, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, dismiss.StatusCode);
    }
}

/// <summary>
/// L4/L5 WebApplicationFactory for the deal review queue. Replaces the Postgres-backed Deals + Catalog
/// registrations with in-memory fakes; the real <c>ReviewDeals</c>/<c>ConfirmDeal</c>/<c>RejectDeal</c>
/// run over them. Seed helpers stage deals directly into the fake repository.
/// </summary>
public sealed class DealReviewFactory : WebApplicationFactory<Program>
{
    private static readonly Guid Store = Guid.NewGuid();
    public Guid MilkProduct { get; } = Guid.NewGuid();
    public Guid BreadProduct { get; } = Guid.NewGuid();
    public Guid UnitId { get; private set; }

    public FakeDealBrowseRepo Repo { get; } = new();
    public FakeReviewProductReader ProductReader { get; } = new();
    public FakeDealStoreReader Stores { get; } = new();
    public FakeReviewMemoryRepo Memories { get; } = new();
    public FakeReviewObservationWriter Observations { get; } = new();
    public FakeReviewUnitRepo Units { get; } = new();
    public FakeReviewCategoryRepo Categories { get; } = new();
    public FakeReviewProductRepo Products { get; } = new();
    public FakeReviewLocationRepo Locations { get; } = new();

    private static readonly IClock Clock = SystemClock.Instance;

    public DealReviewFactory()
    {
        UnitId = Units.Seed("g", "gram");
        ProductReader.Names[MilkProduct] = new DealProductInfo(MilkProduct, "Whole Milk", "Dairy");
        ProductReader.Names[BreadProduct] = new DealProductInfo(BreadProduct, "Sourdough", "Bakery");
        ProductReader.Candidates.Add(new ProductCandidate(MilkProduct, "Whole Milk"));
        ProductReader.Candidates.Add(new ProductCandidate(BreadProduct, "Sourdough"));
        Stores.Names[Store] = "FreshCo";
    }

    public void Reset()
    {
        Repo.Items.Clear();
        Memories.Items.Clear();
        Observations.Calls = 0;
        Products.Items.Clear();
    }

    private static ValidityWindow InWindow()
    {
        var today = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);
        return ValidityWindow.Create(today.AddDays(-1), today.AddDays(6)).Value;
    }

    public Deal SeedPending(string rawName, MatchConfidence confidence, Guid? suggested, decimal price = 4.99m)
    {
        var raw = new RawDeal(rawName, "SomeBrand", null, price, null, null, "Save $1", InWindow());
        var proposal = suggested is { } s
            ? new MatchProposal(s, confidence, "looks like a match")
            : MatchProposal.Unmatched();
        var deal = Deal.Stage(
            HouseholdId.New(), FlyerImportId.New(), Store, raw, DealNormalizer.Normalize(rawName), proposal, Clock);
        Repo.Items.Add(deal);
        return deal;
    }

    /// <summary>
    /// Seeds a pending deal in a bounded window (today−1 .. today+<paramref name="daysUntilExpiry"/>) so tests
    /// can stage two distinct flyers (two windows on the store) and drive the expiry countdown / handoff.
    /// </summary>
    public Deal SeedPendingExpiring(
        string rawName, MatchConfidence confidence, Guid? suggested, int daysUntilExpiry, decimal price = 4.99m)
    {
        var today = DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);
        var window = ValidityWindow.Create(today.AddDays(-1), today.AddDays(daysUntilExpiry)).Value;
        var raw = new RawDeal(rawName, "SomeBrand", null, price, null, null, "Save $1", window);
        var proposal = suggested is { } s
            ? new MatchProposal(s, confidence, "looks like a match")
            : MatchProposal.Unmatched();
        var deal = Deal.Stage(
            HouseholdId.New(), FlyerImportId.New(), Store, raw, DealNormalizer.Normalize(rawName), proposal, Clock);
        Repo.Items.Add(deal);
        return deal;
    }

    public Deal SeedAutoConfirmed(string rawName, Guid product)
    {
        var deal = SeedPending(rawName, MatchConfidence.High, product);
        deal.AutoConfirm(product, Clock);
        return deal;
    }

    public DealMatchMemory SeedMemory(Deal deal, Guid product)
    {
        var normalized = new NormalizedName(deal.NormalizedName, DealNormalizer.NormalizerVersion);
        var memory = DealMatchMemory.Remember(
            deal.HouseholdId, deal.StoreId, normalized, deal.RawName, product, Guid.NewGuid(), Clock);
        Memories.Items.Add(memory);
        return memory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddFakeExpiringSoonHorizon();
            services.AddAuthentication(opts =>
                {
                    opts.DefaultScheme = TestAuthHandler.SchemeName;
                    opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<IDealRepository>();
            services.AddScoped<IDealRepository>(_ => Repo);
            services.RemoveAll<ICatalogProductReader>();
            services.AddScoped<ICatalogProductReader>(_ => ProductReader);
            services.RemoveAll<ICatalogStoreReader>();
            services.AddScoped<ICatalogStoreReader>(_ => Stores);
            services.RemoveAll<IDealMatchMemoryRepository>();
            services.AddScoped<IDealMatchMemoryRepository>(_ => Memories);
            services.RemoveAll<IPriceObservationWriter>();
            services.AddScoped<IPriceObservationWriter>(_ => Observations);

            services.RemoveAll<IUnitRepository>();
            services.AddScoped<IUnitRepository>(_ => Units);
            services.RemoveAll<ICategoryRepository>();
            services.AddScoped<ICategoryRepository>(_ => Categories);
            services.RemoveAll<IProductRepository>();
            services.AddScoped<IProductRepository>(_ => Products);
            services.RemoveAll<ILocationRepository>();
            services.AddScoped<ILocationRepository>(_ => Locations);
        });
    }
}

// ── fakes specific to the review page (Deal repo + store reader are shared from DealsPageTests) ──────

public sealed class FakeReviewProductReader : ICatalogProductReader
{
    public Dictionary<Guid, DealProductInfo> Names { get; } = new();
    public List<ProductCandidate> Candidates { get; } = [];

    public Task<bool> ExistsAsync(Guid productId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ProductCandidate>> ListCandidatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductCandidate>>(Candidates);

    public Task<IReadOnlyDictionary<Guid, DealProductInfo>> ForProductsAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, DealProductInfo> result = productIds
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]);
        return Task.FromResult(result);
    }
}

public sealed class FakeReviewMemoryRepo : IDealMatchMemoryRepository
{
    public List<DealMatchMemory> Items { get; } = [];

    public Task<DealMatchMemory?> FindByKeyAsync(Guid storeId, string normalizedName, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(m => m.StoreId == storeId && m.NormalizedName == normalizedName));

    public Task AddAsync(DealMatchMemory memory, CancellationToken ct = default)
    {
        Items.Add(memory);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewObservationWriter : IPriceObservationWriter
{
    public int Calls { get; set; }

    public Task<Guid> RecordObservationAsync(
        Guid productId, decimal price, decimal? quantity, Guid? unitId, Guid storeId,
        DateOnly validFrom, DateOnly validTo, Guid dealId, Guid? reviewedByUserId,
        DateTimeOffset observedAt, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Guid.NewGuid());
    }
}

public sealed class FakeReviewUnitRepo : IUnitRepository
{
    private readonly List<CatalogUnit> _items = [];

    public Guid Seed(string code, string name)
    {
        var unit = CatalogUnit.Create(
            HouseholdId.From(Guid.NewGuid()), code, name, Dimension.Mass, factorToBase: 1m, isBase: true);
        _items.Add(unit);
        return unit.Id.Value;
    }

    public Task<CatalogUnit?> FindAsync(UnitId id, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Id == id));

    public Task<CatalogUnit?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_items.SingleOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));

    public Task<List<CatalogUnit>> ListAsync(CancellationToken ct = default) => Task.FromResult(_items.ToList());
    public Task AddAsync(CatalogUnit unit, CancellationToken ct = default) { _items.Add(unit); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewCategoryRepo : ICategoryRepository
{
    public Task<Category?> FindAsync(CategoryId id, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<Category?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Category?>(null);
    public Task<List<Category>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task<List<Category>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Category>());
    public Task AddAsync(Category category, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewProductRepo : IProductRepository
{
    public List<Product> Items { get; } = [];

    public Task<Product?> FindAsync(ProductId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.Id == id));

    public Task<Product?> FindByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<List<Product>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListActiveWithSkusAsync(CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ArchivedAt is null).ToList());

    public Task<List<Product>> ListWithConversionsAsync(IEnumerable<ProductId> ids, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => ids.Contains(p.Id)).ToList());

    public Task<List<Product>> ListVariantsAsync(ProductId parentId, CancellationToken ct = default) =>
        Task.FromResult(Items.Where(p => p.ParentProductId == parentId).ToList());

    public Task AddAsync(Product product, CancellationToken ct = default) { Items.Add(product); return Task.CompletedTask; }
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FakeReviewLocationRepo : ILocationRepository
{
    public Task<Location?> FindAsync(LocationId id, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<Location?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Location?>(null);
    public Task<List<Location>> ListAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task<List<Location>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult(new List<Location>());
    public Task AddAsync(Location location, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
