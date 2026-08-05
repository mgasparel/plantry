using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Cook-page "use it up" tests (plantry-1dnk): a per-line affordance anchored to the quantity
/// control — see the ratified prototype (.preview/1dnk-cook-use-it-up.html). Since Alpine's
/// x-show/x-cloak visibility is evaluated client-side (no JS runs under this L4 harness — the
/// same accepted limitation documented on RecipeEditorSnapshotTests.Editor_edit_scale_offer_card),
/// these tests pin what the SERVER controls: which lines carry the pill/chip markup and the
/// on-hand ceiling wiring at all (CookLineView.IsUseUpEligible), and the exact static content
/// (on-hand amounts, unit codes, the Alpine predicate names bound via x-show/:disabled). The
/// sliver-zone/ceiling MATH itself (isInUseUpZone, clampToOnHand, isUsingAllOnHand) is unit-tested
/// directly in cook-logic.test.js. The POST test proves AC7 end-to-end: an accepted "use it up"
/// (a QuantityOverrides entry at the full on-hand amount) really does drive the consume call,
/// via the existing C9 override contract (no new form field).
///
/// Fixture: a recipe with four tracked leaf ingredients at 1 serving (scale=1):
///   • Ground beef — need 3 lb, on hand 3.3 lb  → OK, ELIGIBLE, already a sliver (0.3 left ≤ 10%).
///   • Onion       — need 2 ea, on hand 10 ea   → OK, ELIGIBLE, plenty left (not a sliver).
///   • Carrot      — need 3 ea, on hand 1 ea    → SHORTFALL → excluded (AC5).
///   • Cashew      — need 1 cup, stock 480 g, no g↔cup conversion → UNIT GAP → excluded (AC5).
/// </summary>
public sealed class CookUseItUpTests(CookUseItUpFactory factory, CookConfirmFragmentFactory parentFactory)
    : IClassFixture<CookUseItUpFactory>, IClassFixture<CookConfirmFragmentFactory>
{
    private static readonly HtmlParser Parser = new();

    private async Task<string> GetCookPageAsync()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookUseItUpFixture.HouseholdGuid.ToString());
        var response = await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static IElement RowFor(string pageHtml, string productName)
    {
        var doc = Parser.ParseDocument(pageHtml);
        return doc.QuerySelectorAll(".cook-ing-row")
                   .FirstOrDefault(r => r.TextContent.Contains(productName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No cook-ing-row found for '{productName}'.");
    }

    // ── AC1/AC2: eligible lines carry the pill + chip markup, with the real on-hand amount ────────

    [Fact]
    public async Task Sliver_line_renders_the_pill_with_the_leftover_amount_and_onhand_title()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Ground Beef");

        var pill = row.QuerySelector(".cook-useup");
        Assert.NotNull(pill);
        // Alpine-driven visibility (x-show="showUseUpPill('<lineKey>')") — asserting the exact
        // expression pins that the pill is wired to THIS line's key, not merely present somewhere.
        var groundBeefIngredientId = factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookUseItUpFixture.GroundBeefId).Id.Value;
        Assert.Equal($"showUseUpPill('{groundBeefIngredientId}')", pill!.GetAttribute("x-show"));
        Assert.Contains("Use it up", pill.TextContent, StringComparison.Ordinal);
        // The real on-hand amount (3.3 lb) is server-rendered into the title, not left to JS.
        Assert.Contains("3.3 lb", pill.GetAttribute("title") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sliver_line_renders_the_confirmed_chip_with_the_onhand_amount_and_undo()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Ground Beef");

        var chip = row.QuerySelector(".useup-chip");
        Assert.NotNull(chip);
        Assert.Contains("Using all 3.3 lb on hand", chip!.TextContent, StringComparison.Ordinal);
        var undo = chip.QuerySelector("button");
        Assert.NotNull(undo);
        Assert.Contains("Undo", undo!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plenty_on_hand_line_is_still_eligible_and_carries_pill_and_chip_markup()
    {
        // Onion (10 on hand, need 2) is not currently a sliver, but the affordance's wiring is still
        // present — it's Alpine's showUseUpPill()/showUseUpChip() evaluation (unit-tested in
        // cook-logic.test.js) that decides actual visibility as the user steps the quantity up (AC4).
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Onion");

        Assert.NotNull(row.QuerySelector(".cook-useup"));
        var chip = row.QuerySelector(".useup-chip");
        Assert.NotNull(chip);
        Assert.Contains("Using all 10 ea on hand", chip!.TextContent, StringComparison.Ordinal);
    }

    // ── AC3: stepper ceiling wiring on eligible lines ──────────────────────────────────────────────

    [Fact]
    public async Task Eligible_line_increase_button_disables_at_the_onhand_ceiling()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Ground Beef");

        var increaseBtn = row.QuerySelectorAll(".stepper__btn")
            .FirstOrDefault(b => b.GetAttribute("aria-label") == "Increase");
        Assert.NotNull(increaseBtn);
        Assert.Contains("atUseUpCeiling(", increaseBtn!.GetAttribute(":disabled") ?? "", StringComparison.Ordinal);
    }

    // ── AC5: shortfall and unit-gap lines are entirely excluded (existing states untouched) ───────

    [Fact]
    public async Task Shortfall_line_has_no_pill_no_chip_and_unchanged_ceiling_wiring()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Carrot");

        Assert.Null(row.QuerySelector(".cook-useup"));
        Assert.Null(row.QuerySelector(".useup-chip"));
        // Existing shortfall tag is untouched (AC5).
        Assert.NotNull(row.QuerySelector(".cook-shortfall-tag"));
        Assert.Contains("need 3", row.QuerySelector(".cook-shortfall-tag")!.TextContent, StringComparison.Ordinal);

        var increaseBtn = row.QuerySelectorAll(".stepper__btn")
            .FirstOrDefault(b => b.GetAttribute("aria-label") == "Increase");
        Assert.NotNull(increaseBtn);
        Assert.DoesNotContain("atUseUpCeiling(", increaseBtn!.GetAttribute(":disabled") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unit_gap_line_has_no_pill_no_chip_and_unchanged_ceiling_wiring()
    {
        var html = await GetCookPageAsync();
        var row = RowFor(html, "Cashew");

        Assert.Null(row.QuerySelector(".cook-useup"));
        Assert.Null(row.QuerySelector(".useup-chip"));
        // Existing unit-gap tag is untouched (AC5).
        Assert.NotNull(row.QuerySelector(".cook-unitgap-tag"));

        var increaseBtn = row.QuerySelectorAll(".stepper__btn")
            .FirstOrDefault(b => b.GetAttribute("aria-label") == "Increase");
        Assert.NotNull(increaseBtn);
        Assert.DoesNotContain("atUseUpCeiling(", increaseBtn!.GetAttribute(":disabled") ?? "", StringComparison.Ordinal);
    }

    // ── Parent/variant-picker lines are excluded too (interpretation — see the bead comment trail:
    //    AvailableQuantity there sums across ALL variant children, but a cook only ever consumes
    //    from the ONE selected variant, so there is no single physical container to snap to) ───────

    [Fact]
    public async Task Parent_variant_line_has_no_pill_or_chip_even_though_it_is_not_shortfall()
    {
        // Reuses the existing Cook L4 fixture (CookConfirmFixture): Garlic is a parent product with
        // 5 ea available across its compatible variant (need 3 ea at scale 1) — NOT a shortfall by
        // the numbers, yet must still carry no use-it-up markup because AvailableQuantity there is a
        // sum across variants, not a single on-hand figure a cook could actually snap to.
        var client = parentFactory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookConfirmFixture.HouseholdAId.ToString());
        var html = await (await client.GetAsync($"/Recipes/{parentFactory.RecipeId}/Cook?Servings=4"))
            .Content.ReadAsStringAsync();

        var doc = Parser.ParseDocument(html);
        var garlicRow = doc.QuerySelectorAll(".cook-ing-row")
            .FirstOrDefault(r => r.TextContent.Contains("Garlic", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Garlic row not found.");

        Assert.Null(garlicRow.QuerySelector(".cook-useup"));
        Assert.Null(garlicRow.QuerySelector(".useup-chip"));
    }

    // ── AC7: an accepted "use it up" posts the full on-hand amount via the existing C9 override
    //    contract, and it really does drive the consume call ─────────────────────────────────────

    [Fact]
    public async Task Accepted_use_it_up_consumes_the_full_onhand_amount()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HouseholdHeader, CookUseItUpFixture.HouseholdGuid.ToString());

        var html = await (await client.GetAsync($"/Recipes/{factory.RecipeId}/Cook?Servings=1"))
            .Content.ReadAsStringAsync();
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(tokenMatch.Success, "No antiforgery token on the Cook page.");

        var groundBeefIngredientId = factory.Recipe.Ingredients
            .Single(i => i.ProductId == CookUseItUpFixture.GroundBeefId).Id.Value;

        // Tapping the pill sets the C9 quantity override to the full on-hand amount (3.3 lb) —
        // exactly what useItUp('<key>') does client-side (Cook.cshtml's cookConfirm() factory).
        var response = await client.PostAsync($"/Recipes/{factory.RecipeId}/Cook", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", tokenMatch.Groups[1].Value),
            new("Id", factory.RecipeId.ToString()),
            new("Servings", "1"),
            new($"QuantityOverrides[{groundBeefIngredientId}]", "3.3"),
        ]));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected redirect after successful cook, got {(int)response.StatusCode}.");

        var call = factory.Consumer.Calls.Single(c => c.ProductId == CookUseItUpFixture.GroundBeefId);
        Assert.Equal(3.3m, call.Quantity);
        Assert.Equal(CookUseItUpFixture.PoundUnitId, call.UnitId);
    }
}

// ── Fixture + fakes ──────────────────────────────────────────────────────────────────────────────

/// <summary>Fixture data for the Cook "use it up" tests (plantry-1dnk).</summary>
public static class CookUseItUpFixture
{
    public static readonly Guid HouseholdGuid = Guid.Parse("f1f1f1f1-0000-0000-0000-000000000001");

    public static readonly RecipeId RecipeId = Plantry.Recipes.Domain.RecipeId.From(
        Guid.Parse("f1f1f1f1-0000-0000-0000-000000000002"));

    public static readonly Guid GroundBeefId = Guid.Parse("f2222222-2222-2222-2222-222222222222"); // sliver
    public static readonly Guid OnionId      = Guid.Parse("f3333333-3333-3333-3333-333333333333"); // plenty
    public static readonly Guid CarrotId     = Guid.Parse("f4444444-4444-4444-4444-444444444444"); // shortfall
    public static readonly Guid CashewId     = Guid.Parse("f5555555-5555-5555-5555-555555555555"); // unit gap

    public static readonly Guid PoundUnitId = Guid.Parse("ffffffff-1111-0000-0000-000000000001");
    public static readonly Guid EachUnitId  = Guid.Parse("ffffffff-2222-0000-0000-000000000002");
    public static readonly Guid CupUnitId   = Guid.Parse("ffffffff-3333-0000-0000-000000000003");
    public static readonly Guid GramUnitId  = Guid.Parse("ffffffff-4444-0000-0000-000000000004");

    public static Recipe Build()
    {
        var hid = HouseholdId.From(HouseholdGuid);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(hid, "Use It Up Stew", defaultServings: 1, clock).Value;
        recipe.ReplaceIngredients(
        [
            new IngredientLine(GroundBeefId, 3m, PoundUnitId, GroupHeading: null, Ordinal: 1),
            new IngredientLine(OnionId,      2m, EachUnitId,  GroupHeading: null, Ordinal: 2),
            new IngredientLine(CarrotId,     3m, EachUnitId,  GroupHeading: null, Ordinal: 3),
            new IngredientLine(CashewId,     1m, CupUnitId,   GroupHeading: null, Ordinal: 4),
        ], clock);
        return recipe;
    }

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [GroundBeefId] = new(GroundBeefId, "Ground Beef", TrackStock: true, PoundUnitId, null, IsParent: false, []),
            [OnionId]      = new(OnionId,      "Onion",       TrackStock: true, EachUnitId,  null, IsParent: false, []),
            [CarrotId]     = new(CarrotId,     "Carrot",      TrackStock: true, EachUnitId,  null, IsParent: false, []),
            [CashewId]     = new(CashewId,     "Cashew",      TrackStock: true, GramUnitId,  null, IsParent: false, []),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [PoundUnitId] = "lb",
            [EachUnitId]  = "ea",
            [CupUnitId]   = "cup",
            [GramUnitId]  = "g",
        };

    /// <summary>
    /// Ground beef 3.3 lb (sliver — 0.3 left of 3.3, 10% threshold = 0.33). Onion 10 ea (plenty).
    /// Carrot 1 ea (shortfall — need 3). Cashew 480 g (unit gap — recipe needs cup, no g↔cup path).
    /// </summary>
    public static IReadOnlyDictionary<Guid, ProductStock> Stock() =>
        new Dictionary<Guid, ProductStock>
        {
            [GroundBeefId] = new(GroundBeefId, 3.3m, PoundUnitId, null),
            [OnionId]      = new(OnionId,      10m,  EachUnitId,  null),
            [CarrotId]     = new(CarrotId,     1m,   EachUnitId,  null),
            [CashewId]     = new(CashewId,     480m, GramUnitId,  null),
        };
}

/// <summary>
/// WAF for the Cook "use it up" tests. Wires a <see cref="RecordingFakeCookInventoryConsumer"/> (like
/// <c>CookPostFactory</c> in CookOnPostResolutionTests) so the AC7 POST test can inspect the consume
/// call — real Plantry.Web pipeline, in-memory seams otherwise, mirroring CookUnitGapFactory.
/// </summary>
public sealed class CookUseItUpFactory : WebApplicationFactory<Program>
{
    public Recipe Recipe { get; } = CookUseItUpFixture.Build();
    public Guid RecipeId => Recipe.Id.Value;

    public RecordingFakeCookInventoryConsumer Consumer { get; } = new();

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

            services.RemoveAll<IRecipeRepository>();
            services.AddScoped<IRecipeRepository>(sp =>
                new FakeRecipeRepository(sp.GetRequiredService<ITenantContext>(), Recipe));

            services.RemoveAll<ICatalogProductReader>();
            services.AddSingleton<ICatalogProductReader>(
                new FakeCookCatalogReader(CookUseItUpFixture.Products(), CookUseItUpFixture.UnitCodes()));

            services.RemoveAll<IInventoryStockReader>();
            services.AddSingleton<IInventoryStockReader>(
                new FakeCookStockReader(CookUseItUpFixture.Stock()));

            // Same-unit converts succeed; cross-unit (Cashew: g↔cup) fails — produces the unit gap.
            services.RemoveAll<IUnitConverter>();
            services.AddSingleton<IUnitConverter>(new FakeCookUnitConverter());

            // Substitution reader (plantry-aqpa.3) — empty by default, matching this fixture's existing
            // shape byte-for-byte. Without this override the real Postgres-backed SubstitutionReader
            // resolves, which this no-database factory cannot satisfy.
            services.RemoveAll<ISubstitutionReader>();
            services.AddSingleton<ISubstitutionReader>(new FakeCookSubstitutionReader());
            services.AddFakeQuantityFormatter();

            services.RemoveAll<IInventoryConsumer>();
            services.AddSingleton<IInventoryConsumer>(Consumer);

            services.RemoveAll<ICookEventRepository>();
            services.AddSingleton<ICookEventRepository>(new FakeCookEventRepository());

            // CookRecipe's just-in-time yield-product resolution (plantry-iejb) requires ICatalogWriter —
            // not exercised by these tests, but the WAF must still boot.
            services.RemoveAll<ICatalogWriter>();
            services.AddSingleton<ICatalogWriter>(new FakeCatalogWriter());

            services.RemoveAll<ITagRepository>();
            services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

            services.RemoveAll<IPriceReader>();
            services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));
        });
    }
}
