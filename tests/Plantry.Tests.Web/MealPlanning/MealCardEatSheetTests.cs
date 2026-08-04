using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Identity.Infrastructure;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Tests.Web.MealPlanning;
using Plantry.Tests.Web.Preferences;
using Plantry.Web.MealPlanning;
using Xunit;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for the Eat confirm sheet (plantry-yuy3): the on-hand-aware "use it all"
/// affordance the product-dish Eat action gets, mirroring the Cook page's use-up UX. Reuses the
/// Eat-action fixture/fakes already defined for <c>MealCardEatActionTests</c> (<see
/// cref="EatActionMealPlanRepo"/>, <see cref="SpyEatWriter"/>, <see cref="EatActionFixture"/>) — only
/// the stock reader varies here, since the auto-trigger decision (<see cref="UseUpZone.IsInUseUpZone"/>,
/// unit-tested directly in <c>UseUpZoneTests</c>) is driven by on-hand quantity.
/// </summary>
public sealed class MealCardEatSheetTests
{
    private static readonly HtmlParser Parser = new();

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent AntiforgeryForm(params (string Key, string Value)[] extra)
    {
        var pairs = extra.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToList();
        return new FormUrlEncodedContent(pairs);
    }

    /// <summary>Finds the Eat button (<c>.mc-cook-act.eat</c>, NOT the always-present <c>.mc-cook-act-adjust</c>
    /// secondary control) so auto-trigger assertions check the button that actually varies.</summary>
    private static AngleSharp.Dom.IElement FindEatButton(string html) =>
        Parser.ParseDocument(html).QuerySelector(".mc-cook-act.eat")
            ?? throw new InvalidOperationException("No .mc-cook-act.eat button found.");

    // ── Auto-trigger wiring (server-computed at render time) ─────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan: a dish whose planned qty would leave a <=10% sliver wires Eat to the confirm sheet, not the direct handler")]
    public async Task Sliver_Dish_Wires_Eat_Button_To_The_Confirm_Sheet()
    {
        // Planned qty 2 (EatActionMealPlanRepo's fixed dish), on hand 2.1 -> 0.1 left, 0.1 <= 0.21 (10% of 2.1).
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var html = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var eatButton = FindEatButton(html);

        Assert.Equal(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            eatButton.GetAttribute("hx-get"));
        Assert.Null(eatButton.GetAttribute("hx-post"));
        Assert.Equal("#sheet-host", eatButton.GetAttribute("hx-target"));
        // The manual-override secondary icon control is always present alongside.
        Assert.Contains("mc-cook-act-adjust", html);
    }

    [Fact(DisplayName = "GET /MealPlan: plenty of on-hand stock leaves Eat a plain one-tap POST (no sheet auto-trigger)")]
    public async Task Plentiful_Dish_Keeps_The_Plain_OneTap_Eat_Button()
    {
        // Planned qty 2, on hand 100 -> nowhere near a 10% sliver.
        await using var factory = new EatActionFactory(onHand: 100m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var html = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var eatButton = FindEatButton(html);

        Assert.Equal(
            $"/MealPlan?handler=Eat&plannedDishId={factory.Repo.ProductDishId:D}&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            eatButton.GetAttribute("hx-post"));
        Assert.Null(eatButton.GetAttribute("hx-get"));
        // The manual-override secondary icon control is still present — always reachable regardless
        // of the auto-trigger condition, and it DOES carry hx-get to the sheet (unlike the Eat button
        // itself, asserted above).
        var adjustButton = Parser.ParseDocument(html).QuerySelector(".mc-cook-act-adjust")
            ?? throw new InvalidOperationException("No .mc-cook-act-adjust button found.");
        Assert.Equal(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            adjustButton.GetAttribute("hx-get"));
    }

    // ── GET the sheet ─────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan?handler=EatSheet: returns the sheet prefilled with the planned quantity and real on-hand")]
    public async Task EatSheet_Returns_Sheet_Prefilled_With_Planned_Qty_And_OnHand()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var response = await client.GetAsync(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("How much did you eat?", html);
        Assert.Contains("qty: 2", html); // prefilled to the planned quantity
        Assert.Contains("onHand: 2.1", html);
        Assert.Contains("Flour &middot; 2.1 ea on hand", html);
        Assert.Contains("Use it all", html);
        Assert.Contains($"hx-post=\"/MealPlan?handler=EatConfirm&amp;plannedDishId={factory.Repo.ProductDishId:D}", html);

        // Pin the binding contract: the POST tests below post `quantity` via the query string (form-body
        // decimal parsing is culture-sensitive; query binding is invariant — Gate 10 determinism), so
        // nothing else proves the shipped stepper actually posts a field NAMED "quantity" inside THIS
        // form. A silent rename/drop of the stepper's name would leave the query-string-based POST tests
        // green while the real form confirms quantity=0 (the stepper's own value never travels).
        var doc = Parser.ParseDocument(html);
        var form = doc.QuerySelector("form[hx-post*='handler=EatConfirm']");
        Assert.NotNull(form);
        Assert.NotNull(form!.QuerySelector("input[name='quantity']"));
    }

    [Fact(DisplayName = "GET /MealPlan?handler=EatSheet&week=...: the confirm form's URL carries a single-encoded '&week=' segment, not a double-encoded 'amp;week'")]
    public async Task EatSheet_With_Week_Param_Single_Encodes_The_Week_Segment_In_The_Confirm_Form_Url()
    {
        // Critic pass 2: the `week` segment of OnPostEatConfirmAsync's URL was built from an already
        // HTML-escaped literal ("&amp;week=...") INSIDE a Razor @() expression, so Razor's own encoding
        // pass escaped it a second time, producing "&amp;amp;week=" on the wire — a browser decodes that
        // to a query key literally named "amp;week", so `week` would silently fail to bind. No shipped
        // call site passed `week` (EatSheetVm.Week was always null), which is why nothing caught it —
        // this test drives the GET with an explicit `week` so the confirm form actually has a value to
        // mis-encode.
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var response = await client.GetAsync(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&week={factory.Repo.TodayIso}");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var form = doc.QuerySelector("form[hx-post*='handler=EatConfirm']")
            ?? throw new InvalidOperationException("No EatConfirm form found.");
        // AngleSharp decodes entities when reading an attribute, so a double-encoded URL would surface
        // here as the literal "&amp;week=" — GetAttribute is the reader that would expose that bug.
        var hxPost = form.GetAttribute("hx-post") ?? "";
        Assert.Contains($"&week={factory.Repo.TodayIso}", hxPost);
        Assert.DoesNotContain("&amp;week=", hxPost);
    }

    [Fact(DisplayName = "GET /MealPlan?handler=EatSheet: unauthenticated request is rejected (401)")]
    public async Task EatSheet_Without_Auth_Is_Rejected()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "GET /MealPlan?handler=EatSheet: a dish id from another household resolves to nothing (BadRequest)")]
    public async Task EatSheet_For_Foreign_Household_Dish_Is_Rejected()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());

        var response = await client.GetAsync(
            $"/MealPlan?handler=EatSheet&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST confirm ──────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "POST /MealPlan?handler=EatConfirm: consumes the user-chosen quantity (not the planned amount), swaps the cell, and closes the sheet")]
    public async Task EatConfirm_Consumes_Chosen_Quantity_Swaps_Cell_And_Closes_Sheet()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        // The user snaps to "Use it all" (2.1), not the planned quantity (2).
        var response = await client.PostAsync(
            $"/MealPlan?handler=EatConfirm&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&quantity=2.1",
            AntiforgeryForm(("__RequestVerificationToken", token)));

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var call = Assert.Single(factory.Writer.EatCalls);
        Assert.Equal(factory.Repo.ProductDishId, call.DishId);
        Assert.Equal(factory.Repo.ProductId, call.ProductId);
        Assert.Equal(2.1m, call.Quantity); // the ADJUSTED quantity, not hit.Dish.Servings (2)

        Assert.Contains("mc-cook-done", html);
        // plantry-vqa7: the done row now displays what was ACTUALLY eaten (journal-derived), not the
        // planned Servings (2) — so an adjusted eat of 2.1 renders "Eaten · 2.1 ea" (AC1), proving the
        // display reflects the chosen quantity asserted via the write-port call above, not just the
        // plan. The full "... ea" suffix (not just "Eaten · 2.1") is asserted because this factory's
        // stub genuinely resolves the consumed unit code — a regression that fell back to the
        // unresolved-unit placeholder ("Eaten · 2.1 ?") would otherwise still pass.
        Assert.Contains("Eaten · 2.1 ea", html);

        // The now-stale Eat confirm sheet is closed via the OOB #sheet-host-emptying fragment —
        // this response targets the CELL directly, not #sheet-host, so it needs its own close signal.
        Assert.Contains("id=\"sheet-host\" hx-swap-oob=\"true\"", html);
    }

    [Fact(DisplayName = "POST /MealPlan?handler=EatConfirm: a zero quantity is rejected (BadRequest), never calls the write port")]
    public async Task EatConfirm_With_Zero_Quantity_Is_Rejected()
    {
        // Reachable from the shipped UI (the sheet's decrease control floors at 0.001, but a client could
        // still submit 0) — critic pass 1: 0 must not reach EatAsync, since ProductStock.Consume rejects
        // a non-positive amount and MealPlanEatWriterAdapter only tolerates Inventory.NoStock, so an
        // unguarded 0 would surface as an unhandled 500 instead of a clean BadRequest.
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var response = await client.PostAsync(
            $"/MealPlan?handler=EatConfirm&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&quantity=0",
            AntiforgeryForm(("__RequestVerificationToken", token)));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Writer.EatCalls);
    }

    [Fact(DisplayName = "POST /MealPlan?handler=EatConfirm: unauthenticated request is rejected (401), never calls the write port")]
    public async Task EatConfirm_Without_Auth_Is_Rejected_And_Never_Calls_The_Writer()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync(
            $"/MealPlan?handler=EatConfirm&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&quantity=2.1",
            content: null);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Writer.EatCalls);
    }

    [Fact(DisplayName = "POST /MealPlan?handler=EatConfirm: a dish id from another household resolves to nothing (BadRequest), never calls the write port")]
    public async Task EatConfirm_For_Foreign_Household_Dish_Is_Rejected()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, Guid.NewGuid().ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        var response = await client.PostAsync(
            $"/MealPlan?handler=EatConfirm&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&quantity=2.1",
            AntiforgeryForm(("__RequestVerificationToken", token)));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Writer.EatCalls);
    }

    // ── Round trip: confirm-sheet path composes correctly with the existing (unchanged) Undo handler ──

    [Fact(DisplayName = "Eat via the confirm sheet (adjusted quantity) then Undo: the round trip reverses cleanly and the cell shows the pending Eat button again")]
    public async Task EatConfirm_Then_Undo_Round_Trip_Restores_Pending_State()
    {
        await using var factory = new EatActionFactory(onHand: 2.1m, stubUnitCodes: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(pageHtml);

        (await client.PostAsync(
            $"/MealPlan?handler=EatConfirm&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}&quantity=2.1",
            AntiforgeryForm(("__RequestVerificationToken", token)))).EnsureSuccessStatusCode();

        var undoResponse = await client.PostAsync(
            $"/MealPlan?handler=UndoEat&plannedDishId={factory.Repo.ProductDishId:D}" +
            $"&date={factory.Repo.TodayIso}&slotId={EatActionFixture.LunchSlotId.Value:D}",
            AntiforgeryForm(("__RequestVerificationToken", token)));
        undoResponse.EnsureSuccessStatusCode();
        var html = await undoResponse.Content.ReadAsStringAsync();

        // The existing (unmodified) Undo handler composes correctly with an eat that came from the
        // sheet path — the write port doesn't distinguish where the eat originated (plantry-yuy3 spec:
        // "Undo — no change needed").
        var eatCall = Assert.Single(factory.Writer.EatCalls);
        Assert.Equal(2.1m, eatCall.Quantity);
        Assert.Single(factory.Writer.UndoCalls);

        Assert.Contains("mc-cook-act eat", html);
        Assert.DoesNotContain("mc-cook-done", html);
    }

    // ── plantry-vqa7: mixed-unit fallback ─────────────────────────────────────────────────────────

    [Fact(DisplayName = "GET /MealPlan: a done product dish whose journal movements spanned more than one unit renders 'Eaten' with no quantity, Undo still present")]
    public async Task MixedUnitDoneDish_RendersEatenWithNoQuantity_UndoStillPresent()
    {
        await using var factory = new EatActionFactory(mixedUnitDone: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, EatActionFixture.HouseholdId.ToString());

        var html = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();

        Assert.Contains("mc-cook-done", html);
        // The verb itself must still render — "Eaten" appears nowhere else on /MealPlan
        // (_MealCard.cshtml is its only source), so this catches a regression that emptied the
        // mixed-unit branch entirely (bare check icon + Undo, no text at all).
        Assert.Contains("Eaten", html);
        // The raw net across more than one unit is not a displayable magnitude (plantry-wiv2) — never
        // show a number that could be wrong, so the row reads plain "Eaten", no "· {qty} {unit}".
        Assert.DoesNotContain("Eaten ·", html);
        // Undo stays available regardless of whether a quantity could be displayed (today's eaten
        // product row always gets Undo in place of a timestamp).
        Assert.Contains("class=\"undo\"", html);
    }
}

// ── Fixture ─────────────────────────────────────────────────────────────────────
//
// The WAF factory used by every test above is EatActionFactory (MealCardEatActionTests.cs) — its
// optional onHand/stubUnitCodes/mixedUnitDone constructor args (plantry-yuy3, plantry-vqa7) exist for
// exactly this suite, so a second near-duplicate factory isn't needed here (critic pass 1/3, reuse-first).

/// <summary>Reports a fixed on-hand quantity for exactly one product; every other product has no stock record.</summary>
internal sealed class SingleProductStockReader(Guid productId, decimal onHand, Guid unitId) : IMealPlanStockReader
{
    public Task<MealPlanProductStock?> FindStockAsync(Guid pid, CancellationToken ct = default) =>
        Task.FromResult<MealPlanProductStock?>(
            pid == productId ? new MealPlanProductStock(pid, onHand, unitId, null) : null);
}
