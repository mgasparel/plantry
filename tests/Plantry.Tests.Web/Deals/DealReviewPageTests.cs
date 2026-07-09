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

    /// <summary>GETs with the HX-Request header so the server returns just the #review-region fragment.</summary>
    private static async Task<string> HxGetAsync(HttpClient client, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("HX-Request", "true");
        return await (await client.SendAsync(req)).Content.ReadAsStringAsync();
    }

    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    /// <summary>
    /// The flyer-rail key for a seeded deal's (store, validity-window) — the same key the bulk-verb buttons
    /// thread as <c>&amp;flyer=</c> when an active flyer is present (_ReviewStep1/3.cshtml). Bulk POSTs must
    /// carry it so the exact-key resolution (plantry-vsu4) finds the flyer; a null key is now a no-op.
    /// </summary>
    private static string FlyerKeyOf(Deal deal) =>
        FlyerBlock.MakeKey(deal.StoreId, deal.ValidityWindow.ValidFrom, deal.ValidityWindow.ValidTo);

    // ── L4 render ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /Deals/Review with no pending deals renders the caught-up empty state")]
    public async Task Empty_Queue_Renders_Caught_Up()
    {
        factory.Reset();
        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        Assert.Contains("All caught up", html);
    }

    [Fact(DisplayName = "The step views render each confidence treatment + the single-suggestion chip (q9zr.13)")]
    public async Task Renders_Confidence_Treatments()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough Loaf", MatchConfidence.Low, factory.BreadProduct);
        factory.SeedPending("Mystery Item", MatchConfidence.None, suggested: null);
        var client = AuthedClient();

        // Entry lands on step 1 — the confirmable-High checklist: raw name (verbatim in the title) → suggestion.
        var step1 = await (await client.GetAsync("/Deals/Review")).Content.ReadAsStringAsync();
        Assert.Contains("class=\"steps\"", step1);          // the stepper is present
        Assert.Contains("check-list", step1);               // step 1 is the checklist view
        Assert.Contains("Milk 2L", step1);                  // verbatim High name (title attribute)
        Assert.Contains("Whole Milk", step1);               // its resolved suggestion (→ product)

        // Step 2 — the Low "judgement call": confidence badge, single-suggestion chip, and the single verbs.
        var step2 = await (await client.GetAsync("/Deals/Review?step=2")).Content.ReadAsStringAsync();
        Assert.Contains("Sourdough Loaf", step2);
        Assert.Contains("Check match", step2);              // Low _ConfidenceBadge
        Assert.Contains("Did you mean", step2);             // single-suggestion chip
        Assert.Contains("Confirm", step2);
        Assert.Contains("Correct", step2);
        Assert.Contains("Reject", step2);

        // Step 3 — the None "everything else": no-match badge, verbatim name, no boilerplate.
        var step3 = await (await client.GetAsync("/Deals/Review?step=3")).Content.ReadAsStringAsync();
        Assert.Contains("Mystery Item", step3);
        Assert.Contains("No match", step3);                 // None _ConfidenceBadge
        Assert.DoesNotContain("Unrecognized", step3);
        Assert.DoesNotContain("no catalog match", step3);
    }

    [Fact(DisplayName = "The stepper renders three jumpable step views with live pending counts (q9zr.13)")]
    public async Task Stepper_Renders_Three_Steps_With_Counts()
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

        // The three steps render in fixed order 1 → 2 → 3.
        var s1 = html.IndexOf("Confirm the sure things", StringComparison.Ordinal);
        var s2 = html.IndexOf("Judgement calls", StringComparison.Ordinal);
        var s3 = html.IndexOf("Everything else", StringComparison.Ordinal);
        Assert.True(s1 >= 0 && s2 >= 0 && s3 >= 0, "All three step views should render in the stepper.");
        Assert.True(s1 < s2 && s2 < s3, "Steps must be ordered Confirm → Judgement → Everything.");

        // Each step chip carries its live pending count: step 1 = 2 confirmable Highs, step 2 = 1 Low, step 3 = 3 None.
        Assert.Matches(@"Confirm the sure things\s*<span class=""step__count"">2</span>", html);
        Assert.Matches(@"Judgement calls\s*<span class=""step__count"">1</span>", html);
        Assert.Matches(@"Everything else\s*<span class=""step__count"">3</span>", html);
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

    // ── L4 step-2 judgement-call deck island hydration (q9zr.8) ────────────────────

    [Fact(DisplayName = "Step 2 renders the deck mount + hydration blob (title-cased name, verb URLs) with no rail-context keys")]
    public async Task Step2_Renders_Deck_Island_Hydration()
    {
        factory.Reset();
        // A single Low deal lands the default entry on step 2 (no confirmable Highs). ALL-CAPS proves the
        // server title-cases the display name while keeping the verbatim raw string.
        var deal = factory.SeedPending("BREYERS CREAMERY STYLE ICE CREAM", MatchConfidence.Low, factory.MilkProduct);

        var html = await HxGetAsync(AuthedClient(), "/Deals/Review?step=2");

        // The island mounts here and its data is emitted as a JSON hydration blob.
        Assert.Contains("id=\"judgement-deck\"", html);
        Assert.Contains("data-deck-mount", html);

        // Extract just the hydration JSON so assertions don't leak into the no-JS fallback markup or the rail.
        var m = Regex.Match(html, "<script type=\"application/json\" id=\"deal-deck-data\">(.*?)</script>",
            RegexOptions.Singleline);
        Assert.True(m.Success, "The deck hydration <script id=deal-deck-data> was not rendered.");
        var json = m.Groups[1].Value;

        // Verbatim raw name AND the server title-cased display name (q9zr.10) both travel to the island.
        Assert.Contains("\"rawName\":\"BREYERS CREAMERY STYLE ICE CREAM\"", json);
        Assert.Contains("\"displayName\":\"Breyers Creamery Style Ice Cream\"", json);
        Assert.Contains("\"hasSuggestion\":true", json);
        Assert.Contains(deal.Id.Value.ToString(), json);

        // Every verb still posts through the existing htmx endpoints, threaded onto this step.
        Assert.Contains("handler=Confirm", json);
        Assert.Contains("handler=Reject", json);
        Assert.Contains("step=2", json);

        // "Context lives in the rail" — the card payload carries NO store/dates/confidence-pill keys (final ruling).
        Assert.DoesNotContain("\"storeName\"", json);
        Assert.DoesNotContain("\"validTo\"", json);
        Assert.DoesNotContain("\"validFrom\"", json);
        Assert.DoesNotContain("\"confidence\"", json);
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

    [Fact(DisplayName = "The rail renders a single 'View flyer' link (Flipp store search) when a source flyer resolves — never per card (q9zr.7)")]
    public async Task Renders_View_Flyer_Link_In_Rail_Only()
    {
        factory.Reset();
        // Three deals in one flyer → one big flyer chip. Seed the source-flyer provenance so the link resolves.
        var milk = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("Mystery", MatchConfidence.None, suggested: null);
        factory.SeedFlyerLink(milk, "flipp-freshco-2026-07");

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        // The link points at the verified Flipp store-SEARCH URL (direct flyer-slug URLs 404), opens safely.
        Assert.Contains("class=\"flyer-link\"", html);
        Assert.Contains("href=\"https://flipp.com/en-ca/search/FreshCo\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener\"", html);
        // Exactly one link across the whole region — it lives on the rail chip, not on any of the three cards.
        Assert.Single(Regex.Matches(html, "class=\"flyer-link\""));
    }

    [Fact(DisplayName = "The rail renders no 'View flyer' link when no source flyer resolves for the chapter (q9zr.7)")]
    public async Task Omits_View_Flyer_Link_When_No_Source_Flyer()
    {
        factory.Reset();
        factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct); // no SeedFlyerLink

        var html = await (await AuthedClient().GetAsync("/Deals/Review")).Content.ReadAsStringAsync();

        Assert.Contains("flyer-chip", html);            // the rail still renders
        Assert.DoesNotContain("flyer-link", html);      // but the conditional link slot stays empty
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

    [Fact(DisplayName = "Confirm-finishing a flyer surfaces it as a display-only done chip while the handoff still fires to the next flyer (plantry-8f7v)")]
    public async Task Confirm_Finished_Flyer_Becomes_A_Done_Chip_And_Hands_Off()
    {
        factory.Reset();
        // Two flyers on the rail (distinct windows): the soonest is a single High we will confirm to completion;
        // the later one keeps a pending deal so the handoff has somewhere to point.
        var soon = factory.SeedPendingExpiring("Milk 2L", MatchConfidence.High, factory.MilkProduct, daysUntilExpiry: 2);
        var later = factory.SeedPendingExpiring("Eggs Dozen", MatchConfidence.High, factory.BreadProduct, daysUntilExpiry: 6);
        var soonKey = FlyerBlock.MakeKey(soon.StoreId, soon.ValidityWindow.ValidFrom, soon.ValidityWindow.ValidTo);
        var laterKey = FlyerBlock.MakeKey(later.StoreId, later.ValidityWindow.ValidFrom, later.ValidityWindow.ValidTo);

        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Confirm the only deal in the soonest flyer, carrying its active key — its last verb.
        var response = await PostAsync(client, $"/Deals/Review?handler=Confirm&dealId={soon.Id.Value}&flyer={soonKey}",
            Kv("__RequestVerificationToken", token));

        response.EnsureSuccessStatusCode();
        var fragment = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // The finished flyer persists on the rail as a display-only done chip …
        Assert.Contains("flyer-chip is-done", fragment);
        Assert.Contains("✓ done", fragment);
        // … which is NOT a navigable button — nothing routes back to the finished flyer's key.
        Assert.DoesNotContain($"flyer={soonKey}", fragment);
        // … while the per-flyer handoff simultaneously fires, pointing at the next (still-pending) flyer.
        Assert.Contains("Flyer cleared", fragment);
        Assert.Contains($"flyer={laterKey}", fragment);
        Assert.Equal(DealStatus.Confirmed, soon.Status);   // the finished flyer's deal really left the queue
        Assert.DoesNotContain("All caught up", fragment);  // work remains, so not the empty state
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

    [Fact(DisplayName = "Step 1 commits via the checklist footer (Confirm N matches); step 3 keeps Dismiss all (N)")]
    public async Task Bulk_Buttons_Render_With_Counts()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        var client = AuthedClient();

        // Step 1 (entry): the sticky footer commits the checked matches — server-rendered count = 2 confirmable
        // Highs, both pre-checked. The commit posts checklistCommit + the checked dealIds[] to ConfirmAll.
        var step1 = System.Net.WebUtility.HtmlDecode(
            await (await client.GetAsync("/Deals/Review")).Content.ReadAsStringAsync());
        Assert.Contains("Confirm 2 matches", step1);
        Assert.Contains("name=\"checklistCommit\"", step1);
        // Well-formed handler route (never the un-substituted Razor literal "@flyerQs" — the email-heuristic trap).
        Assert.Matches(@"hx-post=""/Deals/Review\?handler=ConfirmAll(&flyer=[^""]*)?&step=1""", step1);
        Assert.DoesNotContain("@flyerQs", step1);

        // Step 3: the None "everything else" list keeps the Dismiss-all bulk verb with its count.
        var step3 = System.Net.WebUtility.HtmlDecode(
            await (await client.GetAsync("/Deals/Review?step=3")).Content.ReadAsStringAsync());
        Assert.Contains("Dismiss all (1)", step3);
        Assert.Matches(@"hx-post=""/Deals/Review\?handler=DismissAll(&flyer=[^""]*)?&step=3""", step3);
        Assert.DoesNotContain("@flyerQs", step3);
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

        var response = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={FlyerKeyOf(milk)}",
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
        var response = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={FlyerKeyOf(a)}",
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
        var milk = factory.SeedPending("Milk 2L", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("Sourdough", MatchConfidence.High, factory.BreadProduct);
        var flyerKey = FlyerKeyOf(milk);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var first = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={flyerKey}",
            Kv("__RequestVerificationToken", token));
        first.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Observations.Calls);

        // Re-drive: both Highs are gone, so the flyer left the pending projection. Its key now exact-matches
        // nothing → empty eligible set → a clean no-op (plantry-vsu4 strict cleared-flyer replay).
        var second = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={flyerKey}",
            Kv("__RequestVerificationToken", token));
        second.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Observations.Calls);           // no additional writes
        Assert.False(second.Headers.Contains("HX-Trigger"));   // nothing confirmed → no toast
    }

    [Fact(DisplayName = "ConfirmAll carrying a now-cleared flyer key is a strict no-op — never falls through to another flyer's Highs (plantry-vsu4)")]
    public async Task ConfirmAll_With_Cleared_Flyer_Key_Touches_No_Other_Flyer()
    {
        factory.Reset();
        // Two flyers on the same store (distinct windows). X (soonest) has only a confirmable High; Y keeps its
        // own confirmable High. This reproduces the double-submit race: X is cleared, then a stale ConfirmAll
        // still carrying X's key arrives — it must resolve to X exactly (now absent → empty), NOT default to Y.
        var x = factory.SeedPendingExpiring("Milk 2L", MatchConfidence.High, factory.MilkProduct, daysUntilExpiry: 2);
        var y = factory.SeedPendingExpiring("Eggs Dozen", MatchConfidence.High, factory.BreadProduct, daysUntilExpiry: 6);
        var xKey = FlyerBlock.MakeKey(x.StoreId, x.ValidityWindow.ValidFrom, x.ValidityWindow.ValidTo);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // First ConfirmAll on X clears its only High; Y is a chapter away and untouched.
        var first = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={xKey}",
            Kv("__RequestVerificationToken", token));
        first.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Confirmed, x.Status);
        Assert.Equal(DealStatus.Pending, y.Status);
        Assert.Equal(1, factory.Observations.Calls);

        // Replay: X's key is now absent from the pending projection. The stale ConfirmAll must be a strict no-op
        // — exact-key resolution returns empty rather than falling through to Y's (soonest-remaining) Highs.
        var replay = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={xKey}",
            Kv("__RequestVerificationToken", token));
        replay.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Pending, y.Status);            // Y's High was NOT bulk-confirmed
        Assert.Equal(1, factory.Observations.Calls);           // no additional writes
        Assert.False(replay.Headers.Contains("HX-Trigger"));   // nothing confirmed → no toast
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

        var response = await PostAsync(client, $"/Deals/Review?handler=DismissAll&flyer={FlyerKeyOf(real)}",
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

        var response = await PostAsync(client, $"/Deals/Review?handler=DismissAll&flyer={FlyerKeyOf(a)}",
            Kv("__RequestVerificationToken", token),
            Kv("dealIds", a.Id.Value.ToString()),
            Kv("dealIds", c.Id.Value.ToString()));

        response.EnsureSuccessStatusCode();
        Assert.Equal(DealStatus.Rejected, a.Status);
        Assert.Equal(DealStatus.Rejected, c.Status);
        Assert.Equal(DealStatus.Pending, b.Status);            // unrequested None stays pending
    }

    [Fact(DisplayName = "Step 3 renders the sticky filter toolbar + hidden dealIds[] picks that wire the scoped Dismiss (q9zr.5)")]
    public async Task Step3_Renders_Filter_Toolbar_And_Scoped_Dismiss_Wiring()
    {
        factory.Reset();
        var a = factory.SeedPending("Watermelon Chunk", MatchConfidence.None, suggested: null);
        var b = factory.SeedPending("Paper Towels 12pk", MatchConfidence.None, suggested: null);
        var c = factory.SeedPending("Scented Candle", MatchConfidence.None, suggested: null);

        var html = System.Net.WebUtility.HtmlDecode(await HxGetAsync(AuthedClient(), "/Deals/Review?step=3"));

        // The browsable-list host is the non-clipping card (position: sticky needs it) with the filter component.
        Assert.Contains("card--overflow-visible", html);
        Assert.Contains("x-data=\"dealsRestFilter(3)\"", html);      // seeded with the full None count (N = 3)

        // Sticky toolbar: the client-side filter input + the scope-aware bulk Dismiss (relabels client-side via Alpine).
        Assert.Contains("bar-sticky-top rest-toolbar", html);
        Assert.Contains("class=\"rest-filter-input\"", html);
        Assert.Contains("x-model=\"query\"", html);
        Assert.Contains("Dismiss all (3)", html);                    // unfiltered label, server-rendered
        Assert.Contains("hx-include=\".rest-pick\"", html);          // the bulk verb includes only the checked picks

        // One hidden dealIds[] pick per None deal — Alpine checks the visible ones when the filter narrows the set.
        foreach (var deal in new[] { a, b, c })
            Assert.Contains($"class=\"rest-pick\" name=\"dealIds\" value=\"{deal.Id.Value}\"", html);
        Assert.Equal(3, Regex.Matches(html, "class=\"rest-pick\"").Count);

        // The live empty-state scaffold is present (shown by Alpine only when a filter matches nothing).
        Assert.Contains("rest-empty", html);
        Assert.Contains("Nothing matches", html);
    }

    [Fact(DisplayName = "DismissAll a second time after re-render is an idempotent no-op")]
    public async Task DismissAll_Is_Idempotent()
    {
        factory.Reset();
        var noneA = factory.SeedPending("None A", MatchConfidence.None, suggested: null);
        factory.SeedPending("None B", MatchConfidence.None, suggested: null);
        var flyerKey = FlyerKeyOf(noneA);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        var first = await PostAsync(client, $"/Deals/Review?handler=DismissAll&flyer={flyerKey}",
            Kv("__RequestVerificationToken", token));
        first.EnsureSuccessStatusCode();
        Assert.Equal(2, factory.Repo.Items.Count(d => d.Status == DealStatus.Rejected));

        var second = await PostAsync(client, $"/Deals/Review?handler=DismissAll&flyer={flyerKey}",
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

    // ── L4 guided-flow shell — demotion, uncheck persistence, empty steps, idempotency (q9zr.13) ────

    [Fact(DisplayName = "Committing step 1 with a High unchecked confirms the checked ones and demotes the unchecked into step 2 exactly once")]
    public async Task Demotion_Moves_Unchecked_High_Into_Step2_Exactly_Once()
    {
        factory.Reset();
        var a = factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        var b = factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        var low = factory.SeedPending("Low Judgement", MatchConfidence.Low, factory.BreadProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);  // also issues the session cookie (stable flow-store key)

        // Step 1 starts with 2 confirmable Highs; step 2 has the 1 Low.
        var start = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review"));
        Assert.Matches(@"Confirm the sure things\s*<span class=""step__count"">2</span>", start);
        Assert.Matches(@"Judgement calls\s*<span class=""step__count"">1</span>", start);

        // Uncheck A, then commit the checklist with only B checked (checklistCommit + the checked dealIds[]).
        await PostAsync(client, "/Deals/Review?handler=SetCheck",
            Kv("__RequestVerificationToken", token), Kv("dealId", a.Id.Value.ToString()), Kv("isChecked", "false"));

        var commitResponse = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={FlyerKeyOf(a)}&step=1",
            Kv("__RequestVerificationToken", token),
            Kv("checklistCommit", "true"),
            Kv("dealIds", b.Id.Value.ToString()));
        commitResponse.EnsureSuccessStatusCode();
        var afterCommit = System.Net.WebUtility.HtmlDecode(await commitResponse.Content.ReadAsStringAsync());

        // B confirmed (left the queue); A stays Pending but demoted into step 2.
        Assert.Equal(DealStatus.Confirmed, b.Status);
        Assert.Equal(DealStatus.Pending, a.Status);
        Assert.Equal(DealStatus.Pending, low.Status);

        // Step 1 is now empty (✓); step 2 owns the Low + the demoted A = 2; total pending still sums (2).
        Assert.Matches(@"Confirm the sure things\s*<span class=""step__count"">✓</span>", afterCommit);
        Assert.Matches(@"Judgement calls\s*<span class=""step__count"">2</span>", afterCommit);

        // The demoted A appears in step 2 exactly once (counted once, nothing stranded).
        var step2 = await HxGetAsync(client, "/Deals/Review?step=2");
        var demotedOccurrences = Regex.Matches(step2, $"data-deal-id=\"{a.Id.Value}\"").Count;
        Assert.Equal(1, demotedOccurrences);
    }

    [Fact(DisplayName = "An uncheck in step 1 persists across a jump to another step and back (no re-check)")]
    public async Task Uncheck_Persists_Across_A_Step_Round_Trip()
    {
        factory.Reset();
        var a = factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        factory.SeedPending("None C", MatchConfidence.None, suggested: null);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // Both Highs start checked.
        var before = await HxGetAsync(client, "/Deals/Review");
        var checkedBefore = Regex.Matches(before, "checked=\"checked\"").Count;
        Assert.Equal(2, checkedBefore);

        // Uncheck A, jump to step 3, then back to step 1.
        await PostAsync(client, "/Deals/Review?handler=SetCheck",
            Kv("__RequestVerificationToken", token), Kv("dealId", a.Id.Value.ToString()), Kv("isChecked", "false"));
        await HxGetAsync(client, "/Deals/Review?step=3");
        var back = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review?step=1"));

        // The uncheck held — exactly one box checked, and the footer count reflects it. (Without persistence the
        // round-trip would re-check A → "Confirm 2 matches" and two checked boxes: the fixed prototype bug.)
        var checkedAfter = Regex.Matches(back, "checked=\"checked\"").Count;
        Assert.Equal(1, checkedAfter);
        Assert.Contains("Confirm 1 match", back);
    }

    [Fact(DisplayName = "Jumping into an already-cleared step renders the empty-state pointing at a step with work")]
    public async Task Empty_Step_Renders_Jump_Pointer()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        var client = AuthedClient();

        // Steps 2 and 3 are empty (only confirmable Highs). Jump into step 2 → empty-state + pointer to step 1.
        var step2 = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review?step=2"));
        Assert.Contains("All judgement calls resolved", step2);
        Assert.Contains("There is still work in step 1", step2);
        Assert.Contains("Go to step 1", step2);
        Assert.Matches(@"hx-get=""/Deals/Review\?flyer=[^""]*&step=1""", step2);
    }

    [Fact(DisplayName = "?flyer=&step= is refresh-idempotent — a repeated GET lands on the same step with the same content")]
    public async Task Step_Deep_Link_Is_Refresh_Idempotent()
    {
        factory.Reset();
        factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        var low = factory.SeedPending("Low Judgement", MatchConfidence.Low, factory.BreadProduct);
        var key = FlyerBlock.MakeKey(low.StoreId, low.ValidityWindow.ValidFrom, low.ValidityWindow.ValidTo);
        var client = AuthedClient();

        // A deep-link straight to step 2 renders step 2's judgement-call content (the Low), twice, unchanged —
        // a GET never mutates state, so a refresh re-drives to the same view.
        var first = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, $"/Deals/Review?flyer={key}&step=2"));
        var second = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, $"/Deals/Review?flyer={key}&step=2"));

        foreach (var html in new[] { first, second })
        {
            Assert.Contains("Low Judgement", html);                 // step 2 content is shown
            Assert.Matches(@"class=""step is-active""[\s\S]*?step__n"">2", html); // step 2 is the active step
        }
    }

    [Fact(DisplayName = "Committing step 1 with everything unchecked confirms nothing and demotes all Highs to step 2")]
    public async Task Commit_With_Nothing_Checked_Confirms_None_Demotes_All()
    {
        factory.Reset();
        var a = factory.SeedPending("High A", MatchConfidence.High, factory.MilkProduct);
        var b = factory.SeedPending("High B", MatchConfidence.High, factory.BreadProduct);
        var client = AuthedClient();
        var token = await TokenAsync(client);

        // checklistCommit with NO dealIds means "confirm none, demote all" — NOT the legacy empty==whole-set.
        var response = await PostAsync(client, $"/Deals/Review?handler=ConfirmAll&flyer={FlyerKeyOf(a)}&step=1",
            Kv("__RequestVerificationToken", token), Kv("checklistCommit", "true"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(DealStatus.Pending, a.Status);   // nothing confirmed
        Assert.Equal(DealStatus.Pending, b.Status);
        Assert.Equal(0, factory.Observations.Calls);
        Assert.False(response.Headers.Contains("HX-Trigger"));  // no confirm toast

        // Both Highs demoted → step 1 empty, step 2 owns both.
        var frag = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review?step=2"));
        Assert.Matches(@"Judgement calls\s*<span class=""step__count"">2</span>", frag);
    }

    [Fact(DisplayName = "A $0.00 High noise row is excluded from the step-1 checklist and routed to step 2 (honest counts)")]
    public async Task Noise_High_Excluded_From_Step1_Routed_To_Step2()
    {
        factory.Reset();
        factory.SeedPending("Real High", MatchConfidence.High, factory.MilkProduct, price: 4.99m);
        factory.SeedPending("AD MATCH", MatchConfidence.High, factory.BreadProduct, price: 0m); // High but noise
        var client = AuthedClient();

        // Step 1 counts only the confirmable High (== the ConfirmAll-eligible set, so "Confirm N" is honest);
        // the noise High is a judgement call in step 2, where it renders flagged.
        var start = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review"));
        Assert.Matches(@"Confirm the sure things\s*<span class=""step__count"">1</span>", start);
        Assert.Matches(@"Judgement calls\s*<span class=""step__count"">1</span>", start);
        Assert.Contains("Confirm 1 match", start);   // not 2 — the noise row is never checkable in step 1

        var step2 = System.Net.WebUtility.HtmlDecode(await HxGetAsync(client, "/Deals/Review?step=2"));
        Assert.Contains("AD MATCH", step2);
        Assert.Contains("Flyer noise", step2);
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
    public FakeReviewFlyerImportRepo FlyerImports { get; } = new();

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
        FlyerImports.Refs.Clear();
        FlyerImports.ParsedRefsCalls.Clear();
    }

    /// <summary>
    /// Seeds a Parsed source-flyer provenance ref matching a seeded deal's (store, validity-window), so the
    /// review queue resolves a "View flyer" link for that deal's flyer chapter (q9zr.7).
    /// </summary>
    public void SeedFlyerLink(Deal deal, string flyerExternalId) =>
        FlyerImports.Refs.Add(new FlyerImportRef(
            deal.StoreId, deal.ValidityWindow.ValidFrom, deal.ValidityWindow.ValidTo, flyerExternalId));

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
            services.RemoveAll<IFlyerImportRepository>();
            services.AddScoped<IFlyerImportRepository>(_ => FlyerImports);
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

/// <summary>
/// Read-only <see cref="IFlyerImportRepository"/> fake for the review-queue "View flyer" projection (q9zr.7):
/// holds seeded <see cref="FlyerImportRef"/>s and serves the batch resolve. The write/ingest members are
/// unused on the review read path and throw. Records the store-id batches so a test can assert a single batch
/// call (no N+1).
/// </summary>
public sealed class FakeReviewFlyerImportRepo : IFlyerImportRepository
{
    public List<FlyerImportRef> Refs { get; } = [];
    public List<IReadOnlyList<Guid>> ParsedRefsCalls { get; } = [];

    public Task<IReadOnlyList<FlyerImportRef>> ListParsedRefsByStoresAsync(
        IReadOnlyList<Guid> storeIds, CancellationToken ct = default)
    {
        ParsedRefsCalls.Add(storeIds);
        IReadOnlyList<FlyerImportRef> result = Refs.Where(r => storeIds.Contains(r.StoreId)).ToList();
        return Task.FromResult(result);
    }

    public Task<FlyerImport?> FindParsedByDedupKeyAsync(Guid storeId, string flyerExternalId, CancellationToken ct = default) =>
        throw new NotSupportedException("Review read path does not resolve by dedup key.");
    public Task AddAsync(FlyerImport import, CancellationToken ct = default) =>
        throw new NotSupportedException("Review read path does not add imports.");
    public void Detach(FlyerImport import) => throw new NotSupportedException();
    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}
