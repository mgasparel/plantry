using System.Net;
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
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Domain;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web;

/// <summary>
/// Proves the Details "Included recipes" roll-up ROW (plantry-4037, recipe-composition.md §5 — decided
/// design Option C, <c>.preview/included-recipes-redesign.html</c> tab C), replacing the old read-only
/// preview card:
/// <list type="bullet">
/// <item>An inclusion renders IN ORDINAL POSITION inside its authored GroupHeading section, as a row
/// sibling to ingredient lines — the "Chili flakes authored right after an inclusion, same section"
/// stress case from the prototype.</item>
/// <item>The collapsed row carries a worst-of-children status dot + roll-up chip ("1 to buy", tone =
/// worst status), a "N of M tracked in your pantry" sub-label, and a timer chip surfacing the soonest
/// child expiry even while collapsed.</item>
/// <item>Expanding the fold reveals the sub's expanded lines as full-featured rows (status dot, expiry
/// badge) via the SAME <c>_IngredientRow</c> partial a direct ingredient uses.</item>
/// <item>A product duplicated across the parent AND the sub (Garlic here) shows the SAME aggregate
/// verdict in both rows — the shared (ProductId, UnitId) fulfillment grain (D14), not a bug.</item>
/// <item>The servings-stepper OOB refresh (<c>OnGetFulfilmentAsync</c>) re-renders the inclusion row too,
/// since it now lives inside <c>#rd-ing-rows</c> instead of a separate always-static card.</item>
/// </list>
/// </summary>
public sealed class RecipeInclusionRollupRowTests
{
    private static readonly Guid HouseholdGuid = Guid.Parse("f1f1f1f1-0000-0000-0000-000000000001");

    private static readonly Guid ParentId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid SubId = Guid.Parse("a1000000-0000-0000-0000-000000000002");

    private static readonly Guid TomatoesId = Guid.Parse("a2000000-0000-0000-0000-000000000001");
    private static readonly Guid GarlicId = Guid.Parse("a2000000-0000-0000-0000-000000000002");
    private static readonly Guid BasilId = Guid.Parse("a2000000-0000-0000-0000-000000000003");
    private static readonly Guid OreganoId = Guid.Parse("a2000000-0000-0000-0000-000000000004"); // untracked
    private static readonly Guid ChiliFlakesId = Guid.Parse("a2000000-0000-0000-0000-000000000005"); // untracked

    private static readonly Guid GramUnitId = Guid.Parse("a3000000-0000-0000-0000-000000000001");
    private static readonly Guid EachUnitId = Guid.Parse("a3000000-0000-0000-0000-000000000002");

    private static readonly HtmlParser Parser = new();

    // ── Ordinal position: the inclusion sits inside its authored section, sibling ingredient right after ──

    [Fact]
    public async Task Inclusion_renders_in_ordinal_position_inside_its_authored_section()
    {
        using var factory = new RollupFactory();
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");
        var doc = Parser.ParseDocument(html);
        var rows = doc.QuerySelector("#rd-ing-rows")
            ?? throw new InvalidOperationException("#rd-ing-rows not found.");

        // Document order under #rd-ing-rows: "For the sauce" heading, the inclusion fold (Marinara), then
        // Chili flakes (authored right after it, SAME section) — the prototype's stress case (recipe-
        // composition.md §5) — followed by the "To assemble" heading and the direct Garlic row.
        var headingTexts = rows.QuerySelectorAll("h4").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(["For the sauce", "To assemble"], headingTexts);

        var idxSauceHeading = html.IndexOf("For the sauce", StringComparison.Ordinal);
        var idxMarinara = html.IndexOf("Marinara Sauce", StringComparison.Ordinal);
        var idxChili = html.IndexOf("Chili flakes", StringComparison.Ordinal);
        var idxAssembleHeading = html.IndexOf("To assemble", StringComparison.Ordinal);

        Assert.True(idxSauceHeading < idxMarinara, "Inclusion must render under its authored heading.");
        Assert.True(idxMarinara < idxChili, "The inclusion fold must precede the sibling ingredient authored after it.");
        Assert.True(idxChili < idxAssembleHeading, "Chili flakes stays in 'For the sauce', not bleeding into the next section.");
    }

    // ── Collapsed roll-up: worst-of-children status/chip, "N of M tracked", timer chip for hidden expiry ──

    [Fact]
    public async Task Collapsed_row_shows_worst_of_children_chip_and_tracked_sublabel()
    {
        using var factory = new RollupFactory();
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");
        var doc = Parser.ParseDocument(html);
        var fold = doc.QuerySelector("details.rd-sub-fold")
            ?? throw new InvalidOperationException("Inclusion fold not found.");

        // Fold id is stable ("incl-fold-{InclusionId}") so the client-side fold-preserve script (plantry-4037)
        // can re-open it across the stepper's OOB swap.
        Assert.StartsWith("incl-fold-", fold.Id);

        // Worst-of-children status dot: Tomatoes is Missing → the row's own dot is danger-toned.
        var statusBox = fold.QuerySelector(".rd-ing-status")
            ?? throw new InvalidOperationException("Row status box not found.");
        Assert.Contains("rd-ing-status--miss", statusBox.ClassList);

        // Roll-up chip: worst tier only (miss beats low) — "1 to buy" (Tomatoes), not "1 low" (Garlic).
        var chip = fold.QuerySelector(".rollup-chip")
            ?? throw new InvalidOperationException("Roll-up chip not found.");
        Assert.Contains("rollup-chip--miss", chip.ClassList);
        Assert.Equal("1 to buy", chip.TextContent.Trim());

        // Sub-label: "N of M tracked in your pantry" — 1 fully in stock (Basil) of 3 distinct tracked
        // children (Tomatoes, Garlic, Basil; Oregano excluded as untracked).
        var subLabel = fold.QuerySelector(".rd-ing-sub")
            ?? throw new InvalidOperationException("Sub-label not found.");
        Assert.Contains("1 of 3 tracked in your pantry", subLabel.TextContent);

        // Timer chip: Basil expires in 2 days but its own badge is invisible while collapsed — the fold
        // header must still surface it (design item 6) so urgency is never hidden behind the fold.
        var timerChip = fold.QuerySelector("summary .badge-expiry")
            ?? throw new InvalidOperationException("Collapsed timer chip not found.");
        Assert.Contains("in 2d", timerChip.TextContent);
        Assert.Contains("badge-expiry--soon", timerChip.ClassList);
    }

    // ── Expanded: full-featured child rows via the same _IngredientRow partial a direct row uses ─────────

    [Fact]
    public async Task Expanded_fold_renders_full_featured_child_rows()
    {
        using var factory = new RollupFactory();
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");
        var doc = Parser.ParseDocument(html);
        var fold = doc.QuerySelector("details.rd-sub-fold")
            ?? throw new InvalidOperationException("Inclusion fold not found.");
        var body = fold.QuerySelector(".rd-sub-fold__body")
            ?? throw new InvalidOperationException("Fold body not found.");

        // <details> always renders its content server-side (collapsed is a CSS/browser concern, not a
        // server-side omission) — every child line is present, each carrying the accent-bar modifier.
        var childRows = body.QuerySelectorAll(".rd-ing-row").ToList();
        Assert.Equal(4, childRows.Count); // Tomatoes, Garlic, Basil, Oregano
        Assert.All(childRows, r => Assert.Contains("rd-ing-row--sub", r.ClassList));

        var tomatoRow = childRows.Single(r => r.TextContent.Contains("Canned Tomatoes"));
        Assert.Contains("rd-ing-status--miss", tomatoRow.QuerySelector(".rd-ing-status")!.ClassList);

        var basilRow = childRows.Single(r => r.TextContent.Contains("Basil"));
        Assert.Contains("rd-ing-status--have", basilRow.QuerySelector(".rd-ing-status")!.ClassList);
        var basilBadge = basilRow.QuerySelector(".badge-expiry")
            ?? throw new InvalidOperationException("Basil's own expiry badge not found once expanded.");
        Assert.Contains("in 2d", basilBadge.TextContent);

        var oreganoRow = childRows.Single(r => r.TextContent.Contains("Oregano"));
        Assert.Contains("rd-ing-status--untracked", oreganoRow.QuerySelector(".rd-ing-status")!.ClassList);
    }

    // ── Duplicate product across parent + sub: same aggregate verdict in both rows (design item 7, D14) ───

    [Fact]
    public async Task Product_duplicated_across_parent_and_sub_shows_the_same_aggregate_status()
    {
        using var factory = new RollupFactory();
        var client = AuthedClient(factory);

        var html = await client.GetStringAsync($"/Recipes/{ParentId}");
        var doc = Parser.ParseDocument(html);

        // Garlic is a DIRECT ingredient (in "To assemble") AND a child of the Marinara inclusion. Required:
        // 5 ea (direct) + ~0.667 ea (sub, 2/6 factor) ≈ 5.667 ea against 3 ea on hand → Low in BOTH places,
        // because both rows read through the shared (ProductId, UnitId) fulfillment grain.
        var directGarlicRow = doc.QuerySelectorAll("#rd-ing-rows > .rd-ing-row")
            .Single(r => r.TextContent.Contains("Garlic") && !r.ClassList.Contains("rd-sub-fold"));
        Assert.Contains("rd-ing-status--low", directGarlicRow.QuerySelector(".rd-ing-status")!.ClassList);

        var childGarlicRow = doc.QuerySelectorAll(".rd-sub-fold__body .rd-ing-row")
            .Single(r => r.TextContent.Contains("Garlic"));
        Assert.Contains("rd-ing-status--low", childGarlicRow.QuerySelector(".rd-ing-status")!.ClassList);
    }

    // ── Stepper OOB refresh re-renders the inclusion row too — it now lives inside #rd-ing-rows ──────────

    [Fact]
    public async Task Servings_stepper_refresh_re_renders_the_inclusion_row()
    {
        using var factory = new RollupFactory();
        var client = AuthedClient(factory);

        var response = await client.GetAsync($"/Recipes/{ParentId}?handler=Fulfilment&servings=8");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // The OOB #rd-ing-rows block is present and still carries the inclusion fold — proving the roll-up
        // row participates in the same OOB swap direct ingredient rows always have (plantry-4037; before
        // this ticket the "Included recipes" card lived OUTSIDE #rd-ing-rows and never refreshed here).
        Assert.Contains("id=\"rd-ing-rows\"", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
        Assert.Contains("rd-sub-fold", html);
        Assert.Contains("Marinara Sauce", html);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sub-recipe "Marinara Sauce", 6 default servings: Tomatoes (miss), Garlic (contributes to a combined
    /// Low with the parent's direct Garlic line), Basil (in stock but expiring in 2 days), Oregano (untracked).
    /// </summary>
    private static Recipe SubRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Marinara Sauce", defaultServings: 6, clock).Value;
        SetId(recipe, RecipeId.From(SubId));
        recipe.ReplaceIngredients(
        [
            new IngredientLine(TomatoesId, 400m, GramUnitId, GroupHeading: null, Ordinal: 0),
            new IngredientLine(GarlicId, 2m, EachUnitId, GroupHeading: null, Ordinal: 1),
            new IngredientLine(BasilId, 10m, GramUnitId, GroupHeading: null, Ordinal: 2),
            new IngredientLine(OreganoId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 3),
        ], clock);
        return recipe;
    }

    /// <summary>
    /// Parent "Nacho Plate", 4 default servings. "For the sauce": the Marinara inclusion (2 servings, factor
    /// 2/6) at ordinal 0, then Chili flakes (untracked) at ordinal 1 — SAME heading, authored right after the
    /// inclusion (the prototype's deliberate stress case). "To assemble": a direct Garlic line (5 ea) sharing
    /// Garlic's ProductId/UnitId with the sub, so fulfillment aggregates the combined requirement (D14).
    /// </summary>
    private static Recipe ParentRecipe()
    {
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(HouseholdId.From(HouseholdGuid), "Nacho Plate", defaultServings: 4, clock).Value;
        SetId(recipe, RecipeId.From(ParentId));
        var lineSet = RecipeLineSet.Create(
            ingredients:
            [
                new IngredientLine(ChiliFlakesId, Quantity: null, UnitId: null, GroupHeading: "For the sauce", Ordinal: 1),
                new IngredientLine(GarlicId, 5m, EachUnitId, GroupHeading: "To assemble", Ordinal: 2),
            ],
            inclusions:
            [
                new InclusionLine(RecipeId.From(SubId), 2m, "For the sauce", 0),
            ],
            recipe.Id).Value;
        recipe.ReplaceLines(lineSet, clock);
        return recipe;
    }

    private static void SetId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType? // Entity<RecipeId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }

    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, HouseholdGuid.ToString());
        return client;
    }

    /// <summary>Multi-recipe in-memory repository so the real expansion service can resolve the sub.</summary>
    private sealed class TwoRecipeRepo(ITenantContext tenant, params Recipe[] seed) : IRecipeRepository
    {
        private readonly Dictionary<RecipeId, Recipe> _store = seed.ToDictionary(r => r.Id);

        private bool InHousehold(Recipe r) => tenant.HouseholdId is { } hid && r.HouseholdId.Value == hid;

        public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(id, out var r) && InHousehold(r) ? r : null);

        public Task AddAsync(Recipe r, CancellationToken ct = default) { _store[r.Id] = r; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Recipe>>(_store.Values.Where(InHousehold).ToList());
        public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
        public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult(_store.Count > 0);
        public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
            IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(
                ids.Where(_store.ContainsKey).Distinct().ToDictionary(id => id, id => _store[id].Name));
        public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>([]);
        public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
            RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());
    }

    private sealed class RollupFactory : WebApplicationFactory<Program>
    {
        private static readonly IReadOnlyDictionary<Guid, CatalogProduct> Products =
            new Dictionary<Guid, CatalogProduct>
            {
                [TomatoesId] = new(TomatoesId, "Canned Tomatoes", TrackStock: true, GramUnitId, null, IsParent: false, []),
                [GarlicId] = new(GarlicId, "Garlic", TrackStock: true, EachUnitId, null, IsParent: false, []),
                [BasilId] = new(BasilId, "Basil", TrackStock: true, GramUnitId, null, IsParent: false, []),
                [OreganoId] = new(OreganoId, "Oregano", TrackStock: false, GramUnitId, null, IsParent: false, []),
                [ChiliFlakesId] = new(ChiliFlakesId, "Chili flakes", TrackStock: false, GramUnitId, null, IsParent: false, []),
            };

        private static readonly IReadOnlyDictionary<Guid, string> UnitCodes =
            new Dictionary<Guid, string> { [GramUnitId] = "g", [EachUnitId] = "ea" };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddFakeDisplayCurrency();
                services.AddFakeExpiringSoonHorizon(days: 7); // Basil's 2-day expiry falls inside the horizon.
                services.AddAuthentication(opts =>
                    {
                        opts.DefaultScheme = TestAuthHandler.SchemeName;
                        opts.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        opts.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                services.RemoveAll<IRecipeRepository>();
                services.AddScoped<IRecipeRepository>(sp =>
                    new TwoRecipeRepo(sp.GetRequiredService<ITenantContext>(), ParentRecipe(), SubRecipe()));

                services.RemoveAll<ITagRepository>();
                services.AddSingleton<ITagRepository>(new FakeTagRepository(new Dictionary<TagId, string>()));

                services.RemoveAll<ICatalogProductReader>();
                services.AddSingleton<ICatalogProductReader>(new FakeCatalogProductReader(Products, UnitCodes));

                services.RemoveAll<IInventoryStockReader>();
                services.AddSingleton<IInventoryStockReader>(new FakeDetailStockReader(new Dictionary<Guid, ProductStock>
                {
                    // Tomatoes: no stock record → Missing.
                    [GarlicId] = new(GarlicId, 3m, EachUnitId, SoonestExpiry: null), // < 5.667 ea required → Low
                    [BasilId] = new(BasilId, 50m, GramUnitId, SoonestExpiry: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2)),
                }));

                services.RemoveAll<IPriceReader>();
                services.AddSingleton<IPriceReader>(new FakeDetailPriceReader(new Dictionary<Guid, PricePoint>()));

                services.RemoveAll<IUnitConverter>();
                services.AddSingleton<IUnitConverter>(new FakeDetailUnitConverter());
                services.AddFakeQuantityFormatter();

                services.RemoveAll<IShoppingListWriter>();
                services.AddSingleton<IShoppingListWriter>(new NullRollupShoppingWriter());
                services.RemoveAll<IShoppingListRepository>();
                services.AddScoped<IShoppingListRepository, NullRollupShoppingRepo>();
            });
        }
    }

    private sealed class NullRollupShoppingWriter : IShoppingListWriter
    {
        public Task<ShoppingSyncOutcome> SyncSourceContributionAsync(
            IReadOnlyList<ShoppingItem> items, string source, Guid sourceRef, CancellationToken ct = default) =>
            Task.FromResult(ShoppingSyncOutcome.None);
    }

    private sealed class NullRollupShoppingRepo : IShoppingListRepository
    {
        public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
            Task.FromResult<ShoppingList?>(null);
        public Task AddAsync(ShoppingList list, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
