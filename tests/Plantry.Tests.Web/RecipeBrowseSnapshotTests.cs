using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// L4 fragment snapshot tests for the recipe Browse page (P2-2c, J1/J2).
/// Each test fetches the real Browse page as household A (backed by in-memory fakes), extracts a
/// fragment, and verifies it against a committed baseline. Unintended markup changes fail the test.
///
/// <para>The fixture covers all browse render paths (P2-2c acceptance criteria):</para>
/// <list type="bullet">
///   <item>Pancakes — Vegetarian tag, fully in-stock, no expiry → Cook-tonight flag, known cost.</item>
///   <item>Omelette — Spicy tag, in-stock, eggs expiring in 2 days → Use-soon badge, known cost.</item>
///   <item>Milk Shake — no tag, in-stock, no price data → cost cell omitted.</item>
/// </list>
/// </summary>
public sealed class RecipeBrowseSnapshotTests(RecipeBrowseFragmentFactory factory)
    : IClassFixture<RecipeBrowseFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetBrowsePageAsync(string? query = null)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        var url = query is null ? "/Recipes" : $"/Recipes?{query}";
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string Extract(string pageHtml, string selector)
    {
        var doc = Parser.ParseDocument(pageHtml);
        var element = doc.QuerySelector(selector)
            ?? throw new InvalidOperationException($"Selector '{selector}' not found in page HTML.");
        using var writer = new StringWriter();
        element.ToHtml(writer, new PrettyMarkupFormatter());
        return writer.ToString().Replace("\r\n", "\n").Trim();
    }

    // ── Full results container (gallery mode default) ─────────────────────────

    [Fact]
    public async Task Browse_gallery_results()
    {
        var html = await GetBrowsePageAsync();
        await Verify(Extract(html, "#recipes-gallery"), "html");
    }

    // ── Grid mode: trigger with sort=name to get a deterministic stable order ─

    [Fact]
    public async Task Browse_grid_results()
    {
        // x-show on #recipes-grid is Alpine-controlled; we verify the raw markup.
        var html = await GetBrowsePageAsync("sort=name&desc=false");
        await Verify(Extract(html, "#recipes-grid"), "html");
    }

    // ── Use-soon badge renders on the "soon" recipe (Omelette) ───────────────

    [Fact]
    public async Task Browse_gallery_use_soon_badge()
    {
        // The gallery renders Use-soon flag only on Omelette (eggs expiring in 2 days).
        var html = await GetBrowsePageAsync();
        var doc = Parser.ParseDocument(html);
        var flags = doc.QuerySelectorAll(".recipe-card__flag--soon");
        await Verify(string.Join("\n\n", flags.Select(f =>
        {
            using var w = new StringWriter();
            f.ToHtml(w, new PrettyMarkupFormatter());
            return w.ToString().Replace("\r\n", "\n").Trim();
        })), "html");
    }

    // ── NoCost recipe omits cost in gallery and grid ──────────────────────────

    [Fact]
    public async Task Browse_grid_omits_cost_when_none()
    {
        var html = await GetBrowsePageAsync("sort=name&desc=false");
        // Grid row for Milk Shake should have a dash in the cost column, not a price.
        var doc = Parser.ParseDocument(html);
        // Select all cost cells in grid rows
        var costCells = doc.QuerySelectorAll(".recipes-grid__row .recipes-grid__cell--cost");
        await Verify(string.Join("\n\n", costCells.Select(c =>
        {
            using var w = new StringWriter();
            c.ToHtml(w, new PrettyMarkupFormatter());
            return w.ToString().Replace("\r\n", "\n").Trim();
        })), "html");
    }

    // ── Cost currency: a non-USD (EUR) household renders '€' cost cells via MoneyDisplay ──────────

    [Fact(DisplayName = "Browse cost cells render the € symbol for a EUR household (plantry-2x6e.2)")]
    public async Task Browse_cost_uses_household_display_currency()
    {
        using var eurFactory = new RecipeBrowseEurFactory();
        var client = eurFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Recipes?sort=name&desc=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        // The priced grid rows (Pancakes, Omelette) render their cost-per-serving through MoneyDisplay with the
        // household's EUR currency; concatenate the cost cells and assert the '€' symbol, never a hardcoded '$'.
        var costText = string.Concat(
            doc.QuerySelectorAll(".recipes-grid__row .recipes-grid__cell--cost").Select(c => c.TextContent));
        Assert.Contains("€", costText, StringComparison.Ordinal);
        Assert.DoesNotContain("$", costText, StringComparison.Ordinal);
    }

    // ── Rating pills (plantry-zlwp.4) ─────────────────────────────────────────

    [Fact(DisplayName = "Browse gallery: my rating renders the filled 'mine' pill; others-only renders the grey ghost; unrated renders nothing (plantry-zlwp.4)")]
    public async Task Browse_gallery_rating_pills()
    {
        using var ratedFactory = new RecipeBrowseRatedFactory();
        var client = ratedFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RecipeBrowseFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Recipes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var pancakesCard = doc.QuerySelector($"#recipe-card-{ratedFactory.Pancakes.Id.Value}")
            ?? throw new InvalidOperationException("Expected Pancakes gallery card.");
        var omeletteCard = doc.QuerySelector($"#recipe-card-{ratedFactory.Omelette.Id.Value}")
            ?? throw new InvalidOperationException("Expected Omelette gallery card.");
        var milkShakeCard = doc.QuerySelector($"#recipe-card-{ratedFactory.MilkShake.Id.Value}")
            ?? throw new InvalidOperationException("Expected Milk Shake gallery card.");

        // Every positive assertion below targets the specific element under test (class list, text
        // content, or attribute of the element itself) rather than substring-matching InnerHtml — the
        // popover's nested _RecipeRatingBreakdown markup renders .star-rating rows and rating text of
        // its own, so an InnerHtml substring check can stay green after the element it documents is
        // deleted (mutation-blind).

        // Pancakes: I've rated (4) — filled "mine" pill showing my whole number, never the 4.5 decimal
        // avg. Its popover wires aria-describedby to a matching #rating-pop-card-{id} containing my row.
        var pancakesPill = pancakesCard.QuerySelector(".rating-pill")
            ?? throw new InvalidOperationException("Expected Pancakes rating pill.");
        Assert.Contains("rating-pill--mine", pancakesPill.ClassList);
        Assert.Equal("4", pancakesPill.TextContent.Trim());
        var pancakesPopId = $"rating-pop-card-{ratedFactory.Pancakes.Id.Value}";
        var pancakesTrigger = pancakesCard.QuerySelector(".popover__trigger")
            ?? throw new InvalidOperationException("Expected Pancakes rating pill trigger.");
        Assert.Equal(pancakesPopId, pancakesTrigger.GetAttribute("aria-describedby"));
        var pancakesPopover = pancakesCard.QuerySelector($"#{pancakesPopId}")
            ?? throw new InvalidOperationException("Expected Pancakes rating popover content.");
        var pancakesMeRow = pancakesPopover.QuerySelector(".rating-pop-row--me")
            ?? throw new InvalidOperationException("Expected the current user's row in the Pancakes popover.");
        var pancakesMeStars = pancakesMeRow.QuerySelector(".star-rating")
            ?? throw new InvalidOperationException("Expected the current user's stars in the Pancakes popover row.");
        Assert.Equal("4 out of 5 stars", pancakesMeStars.GetAttribute("aria-label"));

        // Omelette: only Alex rated (5) — grey ghost pill showing the decimal avg.
        var omelettePill = omeletteCard.QuerySelector(".rating-pill")
            ?? throw new InvalidOperationException("Expected Omelette rating pill.");
        Assert.Contains("rating-pill--out", omelettePill.ClassList);
        Assert.Equal("5.0", omelettePill.TextContent.Trim());

        // Milk Shake: nobody rated — no rating pill at all.
        Assert.DoesNotContain("rating-pill", milkShakeCard.InnerHtml, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Browse grid: my stars over the household pill; 'not rated by you' when only others have; dash when nobody has (plantry-zlwp.4)")]
    public async Task Browse_grid_rating_cells()
    {
        using var ratedFactory = new RecipeBrowseRatedFactory();
        var client = ratedFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RecipeBrowseFixture.HouseholdAId.ToString());
        var response = await client.GetAsync("/Recipes?sort=name&desc=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var pancakesRow = doc.QuerySelector($"#recipe-row-{ratedFactory.Pancakes.Id.Value}")
            ?? throw new InvalidOperationException("Expected Pancakes grid row.");
        var omeletteRow = doc.QuerySelector($"#recipe-row-{ratedFactory.Omelette.Id.Value}")
            ?? throw new InvalidOperationException("Expected Omelette grid row.");
        var milkShakeRow = doc.QuerySelector($"#recipe-row-{ratedFactory.MilkShake.Id.Value}")
            ?? throw new InvalidOperationException("Expected Milk Shake grid row.");

        // Scope every assertion to the specific ELEMENT under test, not just the rating cell — the cost
        // cell also renders a bare "—" dash (Milk Shake has no price), and the popover breakdown nested
        // INSIDE the rating cell renders .star-rating rows and rating text for other members, so even a
        // cell-scoped InnerHtml/OuterHtml substring check can stay green after the cell's own content
        // (my stars, the muted text, the household pill) is deleted (mutation-blind).
        var pancakesRating = pancakesRow.QuerySelector(".recipes-grid__cell--rating")
            ?? throw new InvalidOperationException("Expected Pancakes rating cell.");
        var omeletteRating = omeletteRow.QuerySelector(".recipes-grid__cell--rating")
            ?? throw new InvalidOperationException("Expected Omelette rating cell.");
        var milkShakeRating = milkShakeRow.QuerySelector(".recipes-grid__cell--rating")
            ?? throw new InvalidOperationException("Expected Milk Shake rating cell.");

        // Pancakes: my stars (4) — the FIRST child of the cell — over the warm --in household pill
        // (4.5 avg, my rating included). Popover wires aria-describedby to a matching #rating-pop-grid-{id}
        // containing my row.
        var pancakesMyStars = pancakesRating.Children[0];
        Assert.Contains("star-rating", pancakesMyStars.ClassList);
        Assert.Equal("4 out of 5 stars", pancakesMyStars.GetAttribute("aria-label"));
        var pancakesGridPill = pancakesRating.QuerySelector(".rating-pill")
            ?? throw new InvalidOperationException("Expected Pancakes grid household pill.");
        Assert.Contains("rating-pill--in", pancakesGridPill.ClassList);
        Assert.Equal("4.5", pancakesGridPill.TextContent.Trim());
        var pancakesGridPopId = $"rating-pop-grid-{ratedFactory.Pancakes.Id.Value}";
        var pancakesGridTrigger = pancakesRating.QuerySelector(".popover__trigger")
            ?? throw new InvalidOperationException("Expected Pancakes grid rating pill trigger.");
        Assert.Equal(pancakesGridPopId, pancakesGridTrigger.GetAttribute("aria-describedby"));
        var pancakesGridPopover = pancakesRating.QuerySelector($"#{pancakesGridPopId}")
            ?? throw new InvalidOperationException("Expected Pancakes grid rating popover content.");
        var pancakesGridMeRow = pancakesGridPopover.QuerySelector(".rating-pop-row--me")
            ?? throw new InvalidOperationException("Expected the current user's row in the Pancakes grid popover.");
        var pancakesGridMeStars = pancakesGridMeRow.QuerySelector(".star-rating")
            ?? throw new InvalidOperationException("Expected the current user's stars in the Pancakes grid popover row.");
        Assert.Equal("4 out of 5 stars", pancakesGridMeStars.GetAttribute("aria-label"));

        // Omelette: "not rated by you" muted text — the FIRST child of the cell, never a star rating —
        // over the grey --out pill (5.0 avg, my rating excluded).
        var omeletteSub = omeletteRating.Children[0];
        Assert.Contains("recipes-grid__cell-sub", omeletteSub.ClassList);
        Assert.Equal("not rated by you", omeletteSub.TextContent.Trim());
        var omeletteGridPill = omeletteRating.QuerySelector(".rating-pill")
            ?? throw new InvalidOperationException("Expected Omelette grid household pill.");
        Assert.Contains("rating-pill--out", omeletteGridPill.ClassList);
        Assert.Equal("5.0", omeletteGridPill.TextContent.Trim());

        // Milk Shake: dash, no pill at all — nobody has rated it.
        Assert.DoesNotContain("rating-pill", milkShakeRating.InnerHtml, StringComparison.Ordinal);
        Assert.Equal("—", milkShakeRating.TextContent.Trim());
    }

    [Fact(DisplayName = "Browse grid: Rating column header sorts by household average, unrated recipes always last (plantry-zlwp.4)")]
    public async Task Browse_grid_rating_sort_nulls_last()
    {
        using var ratedFactory = new RecipeBrowseRatedFactory();
        var client = ratedFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, RecipeBrowseFixture.HouseholdAId.ToString());

        // Descending: Omelette (5.0 avg) before Pancakes (4.5 avg) before Milk Shake (unrated, always last).
        var descResponse = await client.GetAsync("/Recipes?sort=rating&desc=true");
        var descHtml = await descResponse.Content.ReadAsStringAsync();
        var descNames = Parser.ParseDocument(descHtml)
            .QuerySelectorAll(".recipes-grid__name-text b")
            .Select(e => e.TextContent).ToList();
        Assert.Equal(["Omelette", "Pancakes", "Milk Shake"], descNames);

        // Ascending: Pancakes (4.5) before Omelette (5.0); Milk Shake (unrated) STILL last, not first —
        // this is the "nulls last regardless of direction" behaviour the ticket calls for.
        var ascResponse = await client.GetAsync("/Recipes?sort=rating&desc=false");
        var ascHtml = await ascResponse.Content.ReadAsStringAsync();
        var ascNames = Parser.ParseDocument(ascHtml)
            .QuerySelectorAll(".recipes-grid__name-text b")
            .Select(e => e.TextContent).ToList();
        Assert.Equal(["Pancakes", "Omelette", "Milk Shake"], ascNames);
    }

    // ── Toolbar: tag filter chips ─────────────────────────────────────────────

    [Fact]
    public async Task Browse_tag_filter_chips()
    {
        var html = await GetBrowsePageAsync();
        await Verify(Extract(html, ".filter-chip-bar"), "html");
    }

    // ── htmx partial swap: HX-Request returns bare fragment, not full layout ───

    [Fact]
    public async Task Browse_htmx_request_returns_partial_fragment_not_full_page()
    {
        // Simulates a filter/sort htmx request: sends HX-Request header, expects
        // only the results fragment — NOT the full page with <html>/<body>/layout chrome.
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader,
            RecipeBrowseFixture.HouseholdAId.ToString());
        client.DefaultRequestHeaders.Add("HX-Request", "true");

        var response = await client.GetAsync("/Recipes?sort=name&desc=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        // The partial must NOT contain full-page layout elements.
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page-header__title", html, StringComparison.OrdinalIgnoreCase);

        // The partial MUST contain the results region content.
        Assert.True(
            html.Contains("recipes-gallery", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("recipes-grid", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("empty-state", StringComparison.OrdinalIgnoreCase),
            "Response should contain gallery, grid, or empty-state element.");
    }

    // ── Photo thumbnail renders with ?handler=Photo URL (regression guard for HasPhoto branch) ──

    [Fact]
    public async Task Browse_gallery_photo_img_uses_handler_url()
    {
        // Pancakes fixture recipe has a photo set; the gallery must render an <img> whose
        // src uses the ?handler=Photo query-string convention (not the /Photo path form which
        // does not route to the Razor Page handler and causes a 404).
        var html = await GetBrowsePageAsync();
        var doc = Parser.ParseDocument(html);
        var img = doc.QuerySelector(".recipe-card__photo img")
            ?? throw new InvalidOperationException("Expected a recipe-card photo <img> in gallery (Pancakes fixture has a photo).");
        var src = img.GetAttribute("src") ?? string.Empty;
        Assert.Contains("?handler=Photo", src, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Photo", src.Split('?')[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── Unauthenticated request is challenged ─────────────────────────────────

    [Fact]
    public async Task Browse_unauthenticated_is_challenged()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Recipes");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized,
            $"Expected redirect or 401, got {(int)response.StatusCode}");
    }
}
