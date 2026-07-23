using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bogus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Plantry.Deals.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
using Plantry.Pricing.Domain;
using Plantry.Pricing.Infrastructure;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Tenancy;

namespace Plantry.Web.Dev;

/// <summary>
/// Dev-only: seeds a fixed demo household/user plus two random households for multi-tenancy
/// testing. <see cref="SeedAsync"/> is idempotent — no-ops if the demo user already exists.
/// </summary>
public sealed class FakeDataSeeder(
    UserManager<AppUser> userManager,
    IHouseholdRepository householdRepo,
    IEnumerable<IReferenceDataSeeder> seeders,
    TenantContext tenant,
    CatalogDbContext catalogDb,
    PlantryIdentityDbContext identityDb,
    InventoryDbContext inventoryDb,
    RecipesDbContext recipesDb,
    MealPlanningDbContext mealPlanningDb,
    PricingDbContext pricingDb,
    DealsDbContext dealsDb,
    IStoreRepository storeRepo,
    ConfirmDeal confirmDeal,
    RejectDeal rejectDeal,
    IClock clock)
{
    public const string DemoEmail = "demo@plantry.dev";
    public const string DemoPassword = "demo1234";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await userManager.FindByEmailAsync(DemoEmail) is not null)
            return;

        await CreateHouseholdWithUserAsync(
            "The Demo Household", DemoEmail, DemoPassword,
            new Faker("en") { Random = new Randomizer(42) }, ct,
            // Extra members give the demo household a real roster for the meal-slot attendee
            // picker (J2) and other per-member features. Fixed names keep the seed reproducible.
            extraMemberNames: ["Sam", "Alex", "Jordan"]);

        for (int i = 0; i < 2; i++)
        {
            var f = new Faker("en");
            await CreateHouseholdWithUserAsync(
                $"The {f.Name.LastName()} Household",
                f.Internet.Email(),
                "Password123!",
                f, ct);
        }
    }

    public async Task ResetAndSeedAsync(CancellationToken ct = default)
    {
        await DeleteAllAsync(ct);
        await SeedAsync(ct);
    }

    private async Task CreateHouseholdWithUserAsync(
        string householdName, string email, string password, Faker faker, CancellationToken ct,
        string[]? extraMemberNames = null)
    {
        // RegisterHouseholdCommand creates the household and seeds reference data (units,
        // categories, locations). It arms/clears tenant internally for the reference-data inserts.
        var cmd = new RegisterHouseholdCommand(householdName, clock, householdRepo, seeders, tenant);
        var result = await cmd.ExecuteAsync(ct);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Failed to create household '{householdName}': {result.Error.Description}");

        var householdId = result.Value;

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = faker.Name.FirstName(),
            HouseholdId = householdId.Value,
        };
        var identityResult = await userManager.CreateAsync(user, password);
        if (!identityResult.Succeeded)
            throw new InvalidOperationException(string.Join(", ", identityResult.Errors.Select(e => e.Description)));

        await userManager.AddClaimAsync(user, new Claim(HouseholdIdClaims.ClaimType, householdId.Value.ToString()));

        var userId = Guid.Parse(user.Id);

        // Additional household members. They share the household so they appear in the roster
        // (IHouseholdMemberReader) used by the meal-slot attendee picker; each can sign in too.
        var emailDomain = email.Split('@')[^1];
        foreach (var memberName in extraMemberNames ?? [])
        {
            var memberEmail = $"{memberName.ToLowerInvariant()}@{emailDomain}";
            var member = new AppUser
            {
                UserName = memberEmail,
                Email = memberEmail,
                DisplayName = memberName,
                HouseholdId = householdId.Value,
            };
            var memberResult = await userManager.CreateAsync(member, password);
            if (!memberResult.Succeeded)
                throw new InvalidOperationException(string.Join(", ", memberResult.Errors.Select(e => e.Description)));

            await userManager.AddClaimAsync(member, new Claim(HouseholdIdClaims.ClaimType, householdId.Value.ToString()));
        }

        // Whether this is the demo household (fixed email → fixed rich seeding).
        var isDemo = email == DemoEmail;

        // Arm both the Postgres GUC (via TenantContext → interceptor) and the EF query filter
        // (via SetHouseholdId), mirroring what RlsMiddleware does for authenticated requests.
        ArmTenant(householdId.Value);
        try
        {
            await SeedProductsAsync(householdId, ct);
            // Recipes reference the products just committed above, so they must seed after them.
            // Tags are already in place — RegisterHouseholdCommand ran RecipesReferenceDataSeeder.
            await SeedRecipesAsync(householdId, ct);
            // Inventory must seed after products (needs product ids + default units/locations).
            await SeedInventoryAsync(householdId, userId, ct);
            // Price observations must seed after products (needs product ids + unit ids).
            // Only the demo household gets rich pricing data; random households skip this.
            if (isDemo)
                await SeedPriceObservationsAsync(householdId, userId, ct);
            // Meal plan must seed after recipes (references their ids), after the slot config that
            // RegisterHouseholdCommand created (Breakfast/Lunch/Dinner), and after price observations
            // are in place so cost roll-ups produce real figures on first load.
            // For the demo household, also seeds slot default attendees and per-meal overrides.
            await SeedMealPlanAsync(householdId, userId, isDemo, ct);
            // Deals + flyer-review data (plantry-q9zr.14) — demo household only (the review UX is a demo
            // surface). Seeds after products so suggested matches resolve against the demo catalog; the
            // fixture is a real post-match export, replayed WITHOUT the AI matcher.
            if (isDemo)
                await SeedDealsAsync(householdId, userId, ct);
        }
        finally
        {
            DisarmTenant();
        }
    }

    private async Task SeedProductsAsync(HouseholdId householdId, CancellationToken ct)
    {
        var units = await catalogDb.Units.ToDictionaryAsync(u => u.Code, ct);
        var categories = await catalogDb.Categories.ToDictionaryAsync(c => c.Name, ct);
        var locations = await catalogDb.Locations.ToDictionaryAsync(l => l.Name, ct);

        // Build the flat product list first (SKUs + conversions, no variant links yet).
        var allProducts = BuildProducts(householdId, units, categories, locations);
        var productsByName = allProducts.ToDictionary(p => p.Name);

        // Create parent products only for groups that have at least 2 variants in this seed set.
        // The self-referential composite FK (household_id, parent_product_id) → (household_id, id)
        // is non-deferrable, so parents must be committed before variants can reference them.
        var parents = new Dictionary<string, Product>();
        if (units.TryGetValue("ea", out var each))
        {
            foreach (var (parentName, variantNames, categoryName) in VariantGroupDefinitions)
            {
                if (variantNames.Count(n => productsByName.ContainsKey(n)) < 2) continue;

                var parent = Product.Create(householdId, parentName, each.Id, clock);
                if (categories.TryGetValue(categoryName, out var cat))
                    parent.SetCategory(cat.Id, clock);
                parents[parentName] = parent;
            }
        }

        if (parents.Count > 0)
        {
            await catalogDb.Products.AddRangeAsync(parents.Values, ct);
            await catalogDb.SaveChangesAsync(ct);
        }

        // Now link variants. Parents are in the DB so the FK is satisfied.
        // The has_variants updates on the (already-tracked) parent entities will be flushed
        // by the second SaveChangesAsync below alongside the variant inserts.
        foreach (var product in allProducts)
        {
            if (!VariantGroupMap.TryGetValue(product.Name, out var parentName)) continue;
            if (!parents.TryGetValue(parentName, out var parent)) continue;

            product.MakeVariantOf(parent.Id, clock);
            product.InheritFrom(parent, clock);
            if (!parent.HasVariants)
                parent.SetHasVariants(true, clock);
        }

        await catalogDb.Products.AddRangeAsync(allProducts, ct);
        await catalogDb.SaveChangesAsync(ct);
    }

    private async Task SeedRecipesAsync(HouseholdId householdId, CancellationToken ct)
    {
        // Resolve the products + tags this household already has (seeded above / by the reference
        // seeder). Both reads honour the armed query filter, so they only ever see this household.
        var products = await catalogDb.Products.ToDictionaryAsync(p => p.Name, ct);
        var tags = await recipesDb.Tags.ToDictionaryAsync(t => t.Name, ct);

        var recipes = new List<Recipe>();
        foreach (var seed in RecipeSeeds)
        {
            // Defensive: skip a recipe whose ingredients aren't all in the seeded catalog.
            if (seed.Lines.Any(l => !products.ContainsKey(l.Product)))
                continue;

            var created = Recipe.Create(householdId, seed.Name, seed.Servings, clock);
            if (created.IsFailure)
                throw new InvalidOperationException(
                    $"Seed recipe '{seed.Name}' is invalid: {created.Error.Description}");

            var recipe = created.Value;
            recipe.SetSource(seed.Source, clock);
            recipe.SetCookTime(seed.CookTimeMinutes, clock);
            recipe.SetDirections(seed.Directions, clock);

            // Each line is measured in its product's default unit, so no conversion path is ever
            // required (R7 is an app-layer concern; here we sidestep it by construction). A null
            // quantity is a "to taste" line — quantity and unit are then both null per R5.
            var lines = seed.Lines.Select((l, i) =>
            {
                var product = products[l.Product];
                // Fail-loud seed guard: a tracked product with a null ("to taste") quantity would
                // yield a recipe AuthorRecipe's R5 rule rejects on save. Catch it here, not at Save.
                AssertSeedLineSatisfiesR5(product, l.Qty, seed.Name);
                Guid? unit = l.Qty.HasValue ? product.DefaultUnitId.Value : null;
                return new IngredientLine(product.Id.Value, l.Qty, unit, l.Group, i);
            }).ToList();

            var replaced = recipe.ReplaceIngredients(lines, clock);
            if (replaced.IsFailure)
                throw new InvalidOperationException(
                    $"Seed recipe '{seed.Name}' ingredients are invalid: {replaced.Error.Description}");

            var tagIds = seed.Tags.Where(tags.ContainsKey).Select(t => tags[t].Id).ToList();
            recipe.SetTags(tagIds, clock);

            recipes.Add(recipe);
        }

        // Parent-recipe-with-inclusion scenario (plantry-abq5) — closes a demo-parity gap: the seeder
        // predates plantry-4037 (inclusion lines as collapsible roll-up rows) and shipped zero inclusion
        // recipes, so nothing here exercised that UI. Appended separately from the RecipeSeeds loop above
        // because RecipeSeed only carries IngredientLines; Inclusion needs a second, already-created
        // Recipe to reference (RecipeLineSet.Create's N1/N2 + the shared ordinal space, recipe-composition.md §3).
        recipes.AddRange(BuildInclusionScenario(householdId, products, tags, clock));

        await recipesDb.Recipes.AddRangeAsync(recipes, ct);
        await recipesDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stable, well-known names for the parent-recipe-with-inclusion demo scenario (plantry-abq5) —
    /// referenced by seed-consuming E2E tests, so keep these in sync with any rename here.
    /// </summary>
    internal const string InclusionSubRecipeName = "Basic Tomato Sauce";
    internal const string InclusionParentRecipeName = "Beef Meatballs with Tomato Sauce";

    /// <summary>
    /// Builds the sub-recipe ("Basic Tomato Sauce", its own direct ingredients) and the parent recipe
    /// ("Beef Meatballs with Tomato Sauce", direct ingredients PLUS one <see cref="Inclusion"/> line for
    /// the sub) under the stable names above. "Garlic" and "Olive oil" deliberately appear in both the
    /// parent's direct lines and the sub's lines — plantry-4037's documented duplicate-product case (both
    /// rows show the aggregate verdict on the (ProductId, UnitId) grain) — so that scenario has seeded
    /// coverage too. Both recipes are added to (not saved by) the caller's <c>recipes</c> list; the
    /// sub-recipe's <see cref="RecipeId"/> is minted in-memory by <see cref="Recipe.Create"/>, and
    /// <c>recipe_inclusion.sub_recipe_id</c> is a bare soft-reference (no FK, RecipesDbContext), so the
    /// two can be inserted in either order within the same SaveChanges.
    /// </summary>
    private List<Recipe> BuildInclusionScenario(
        HouseholdId householdId,
        Dictionary<string, Product> products,
        Dictionary<string, Tag> tags,
        IClock clock)
    {
        // Defensive: this scenario depends on a fixed set of ProductTemplates entries. Fail loud (not the
        // RecipeSeeds loop's silent skip) — an E2E scope item depends on this recipe always being present.
        string[] required =
        [
            "Chopped tomatoes", "Onions", "Garlic", "Olive oil", "Oregano", "Sea salt",
            "Beef mince", "Breadcrumbs",
        ];
        var missing = required.Where(n => !products.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Inclusion demo scenario seed data references products missing from the catalog: " +
                $"{string.Join(", ", missing)}. Check ProductTemplates.");

        Recipe BuildRecipe(
            string name, string? source, int? cookTimeMinutes, int servings, string[] tagNames,
            (string Product, decimal? Qty, string? Group)[] lines, string directions,
            IReadOnlyList<InclusionLine> inclusions)
        {
            var created = Recipe.Create(householdId, name, servings, clock);
            if (created.IsFailure)
                throw new InvalidOperationException(
                    $"Inclusion demo seed recipe '{name}' is invalid: {created.Error.Description}");

            var recipe = created.Value;
            recipe.SetSource(source, clock);
            recipe.SetCookTime(cookTimeMinutes, clock);
            recipe.SetDirections(directions, clock);

            var ingredientLines = lines.Select((l, i) =>
            {
                var product = products[l.Product];
                AssertSeedLineSatisfiesR5(product, l.Qty, name);
                Guid? unit = l.Qty.HasValue ? product.DefaultUnitId.Value : null;
                return new IngredientLine(product.Id.Value, l.Qty, unit, l.Group, i);
            }).ToList();

            // Inclusions share the ordinal space with ingredients (N3) — they continue numbering
            // immediately after the direct ingredient lines built above.
            var offsetInclusions = inclusions
                .Select((inc, i) => inc with { Ordinal = ingredientLines.Count + i })
                .ToList();

            var lineSet = RecipeLineSet.Create(ingredientLines, offsetInclusions, recipe.Id);
            if (lineSet.IsFailure)
                throw new InvalidOperationException(
                    $"Inclusion demo seed recipe '{name}' lines are invalid: {lineSet.Error.Description}");

            recipe.ReplaceLines(lineSet.Value, clock);

            var tagIds = tagNames.Where(tags.ContainsKey).Select(t => tags[t].Id).ToList();
            recipe.SetTags(tagIds, clock);

            return recipe;
        }

        var sub = BuildRecipe(
            InclusionSubRecipeName, "Plantry kitchen", 20, 4,
            ["Vegan", "Vegetarian"],
            [
                ("Chopped tomatoes", 800m, null),
                ("Onions",           150m, null),
                ("Garlic",            10m, null),
                ("Olive oil",         30m, null),
                ("Oregano",            5m, null),
                ("Sea salt",         null, null),
            ],
            """
            Heat the olive oil and soften the diced onion for 5 minutes.

            Stir in the garlic and oregano and cook until fragrant.

            Add the chopped tomatoes and simmer for 15 minutes until thickened.

            Season to taste.
            """,
            inclusions: []);

        var parent = BuildRecipe(
            InclusionParentRecipeName, "Plantry kitchen", 45, 4,
            ["Meat"],
            [
                ("Beef mince",   500m, null),
                ("Breadcrumbs",   50m, null),
                ("Garlic",        10m, null),
                ("Olive oil",     15m, null),
            ],
            """
            Combine the beef mince, breadcrumbs and garlic; shape into meatballs.

            Fry the meatballs in the olive oil until browned all over.

            Add the tomato sauce and simmer the meatballs through for 15 minutes.

            Serve with the sauce spooned over.
            """,
            // One full batch (all 4 servings) of the sub-recipe. Ordinal reassigned in BuildRecipe above.
            inclusions: [new InclusionLine(sub.Id, Servings: 4m, GroupHeading: null, Ordinal: 0)]);

        return [sub, parent];
    }

    private List<Product> BuildProducts(
        HouseholdId householdId,
        Dictionary<string, Unit> units,
        Dictionary<string, Category> categories,
        Dictionary<string, Location> locations)
    {
        var products = new List<Product>();

        // Seed every template name deterministically. A random subset (the old behaviour) made the
        // catalog non-reproducible and could omit products the sample receipt resolves against —
        // e.g. the "Did you mean" alternatives (Butter / Margarine / Avocado Spread) silently vanish
        // when their products aren't seeded. See SampleReceiptParser for the names this must cover.
        foreach (var (categoryName, tmpl) in ProductTemplates)
        {
            if (!categories.TryGetValue(categoryName, out var cat)) continue;
            if (!units.TryGetValue(tmpl.DefaultUnit, out var unit)) continue;

            foreach (var name in tmpl.Names)
            {
                // Untracked staples (salt/pepper "to taste") are derived from recipe usage — see
                // UntrackedStapleNames. Minting them tracked would produce seed recipes that
                // AuthorRecipe's R5 rule rejects on save (a tracked line needs qty + unit).
                var product = Product.Create(householdId, name, unit.Id, clock,
                    trackStock: !UntrackedStapleNames.Contains(name));
                product.SetCategory(cat.Id, clock);

                if (tmpl.DefaultLocation is { } locName && locations.TryGetValue(locName, out var loc))
                    product.SetDefaultLocation(loc.Id, clock);
                if (tmpl.DueDays is { } due)
                    product.SetExpiryDefaults(due, null, null, null, clock);

                if (SkuTemplates.TryGetValue(name, out var skuDefs))
                    foreach (var (label, qty, unitCode) in skuDefs)
                        product.AddSku(label, qty,
                            unitCode is not null && units.TryGetValue(unitCode, out var su) ? su.Id : null,
                            clock);

                if (ConversionTemplates.TryGetValue(name, out var convDefs))
                    foreach (var (fromCode, toCode, factor) in convDefs)
                        if (units.TryGetValue(fromCode, out var fromUnit) && units.TryGetValue(toCode, out var toUnit))
                            product.AddConversion(fromUnit.Id, toUnit.Id, factor, clock);

                products.Add(product);
            }
        }

        return products;
    }

    private async Task SeedInventoryAsync(HouseholdId householdId, Guid userId, CancellationToken ct)
    {
        // Load only leaf products — parents (HasVariants = true) cannot hold stock directly.
        var products = await catalogDb.Products
            .Where(p => !p.HasVariants)
            .ToDictionaryAsync(p => p.Name, ct);

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Products absent from this dict are intentionally Missing (no ProductStock created).
        // "Bell peppers" and "Frozen berries" are omitted to produce two Missing ingredients
        // across the seeded recipes.
        var stockPlan = new Dictionary<string, (decimal Qty, DateOnly? Expiry)>
        {
            // Chicken & Chickpea Curry
            ["Chicken breast"]   = (800m,  today.AddDays(2)),   // InStock — expiring soon (2 d)
            ["Onions"]           = (500m,  null),
            ["Garlic"]           = (50m,   null),
            ["Chopped tomatoes"] = (1200m, null),
            ["Coconut milk"]     = (800m,  null),
            ["Chickpeas"]        = (800m,  null),
            ["Cumin — ground"]   = (50m,   null),
            ["Paprika — smoked"] = (30m,   null),
            ["Olive oil"]        = (300m,  null),
            // Beef Chilli con Carne (Bell peppers intentionally missing)
            ["Beef mince"]       = (600m,  null),
            ["Red kidney beans"] = (400m,  null),
            ["Cayenne pepper"]   = (20m,   null),
            // Salmon & Vegetable Traybake (Bell peppers missing; Salmon and Broccoli low)
            ["Salmon fillet"]    = (150m,  null),   // Low — recipe needs 280 g
            ["Potatoes"]         = (600m,  null),
            ["Broccoli"]         = (100m,  null),   // Low — recipe needs 200 g
            ["Lemons"]           = (200m,  null),
            // Chickpea & Spinach Stew
            ["Spinach"]          = (250m,  today.AddDays(3)),   // InStock — expiring soon (3 d)
            // Greek Yogurt Overnight Oats (Frozen berries intentionally missing)
            ["Rolled oats"]      = (200m,  null),
            ["Whole milk"]       = (1m,    null),               // 1 l
            ["Greek yogurt"]     = (0.3m,  today.AddDays(-3)), // InStock — already expired (0 d badge)
        };

        var stocks = new List<ProductStock>();
        foreach (var (name, (qty, expiry)) in stockPlan)
        {
            if (!products.TryGetValue(name, out var product)) continue;

            var locationId = product.DefaultLocationId?.Value
                ?? throw new InvalidOperationException($"Seed product '{name}' has no default location.");

            var stock = ProductStock.Start(householdId, product.Id.Value, clock);
            stock.AddStock(qty, product.DefaultUnitId.Value, locationId, userId, clock,
                expiryDate: expiry);
            stocks.Add(stock);
        }

        await inventoryDb.ProductStocks.AddRangeAsync(stocks, ct);
        await inventoryDb.SaveChangesAsync(ct);
    }

    private async Task SeedMealPlanAsync(HouseholdId householdId, Guid userId, bool seedAttendees, CancellationToken ct)
    {
        // The slot config (Breakfast/Lunch/Dinner) was created by MealPlanningReferenceDataSeeder
        // during RegisterHouseholdCommand. Load it to resolve slot ids by label.
        var config = await mealPlanningDb.MealSlotConfigs
            .Include(c => c.Slots)
            .FirstOrDefaultAsync(ct);
        if (config is null) return;

        MealSlot? Slot(string label) =>
            config.Slots.FirstOrDefault(s => s.Label == label && s.IsActive);

        // For the demo household, assign default attendees to each slot so slot bands render
        // with avatar stacks. Load all household member IDs from the identity store.
        List<Guid> allMemberIds = [];
        if (seedAttendees)
        {
            var memberUsers = await identityDb.Users
                .Where(u => u.HouseholdId == householdId.Value)
                .Select(u => u.Id)
                .ToListAsync(ct);

            allMemberIds = memberUsers.Select(Guid.Parse).ToList();

            // Dinner: everyone eats together — all members.
            if (Slot("Dinner") is { } dinnerSlot)
                config.SetDefaultAttendees(dinnerSlot.Id, allMemberIds, clock);

            // Breakfast: only the first two members (e.g. adults who eat breakfast).
            // Lunch: first two members.
            var morningCrew = allMemberIds.Count >= 2 ? allMemberIds.Take(2).ToList() : allMemberIds;
            if (Slot("Breakfast") is { } breakfastSlot)
                config.SetDefaultAttendees(breakfastSlot.Id, morningCrew, clock);
            if (Slot("Lunch") is { } lunchSlot)
                config.SetDefaultAttendees(lunchSlot.Id, morningCrew, clock);

            await mealPlanningDb.SaveChangesAsync(ct);
        }

        // Recipes seeded above, resolved by name.
        var recipes = await recipesDb.Recipes.ToDictionaryAsync(r => r.Name, ct);

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var monday = MealPlan.NormalizeToMonday(today);
        var plan = MealPlan.Start(householdId, monday, clock);

        void AssignRecipe(int dayOffset, string slotLabel, string recipeName, int servings,
            List<Guid>? attendeesOverride = null)
        {
            if (!recipes.TryGetValue(recipeName, out var recipe)) return;
            if (Slot(slotLabel) is not { } slot) return;
            plan.AssignMeal(
                monday.AddDays(dayOffset), slot.Id,
                [new DishSpec(DishKind.Recipe, recipe.Id.Value, servings)],
                attendeesOverride: attendeesOverride, source: "manual", createdBy: userId, clock);
        }

        // Build a "kids-only" override for the demo (first member only, to differ from the slot default).
        // This exercises the "overrides slot default" badge and distinct avatar stack in the editor/card.
        List<Guid>? kidsOnlyOverride = seedAttendees && allMemberIds.Count >= 3
            ? [allMemberIds[^1]]        // last member (e.g. Jordan) only — differs from Dinner default
            : null;

        // A representative half-full week: a mix of dinners, a lunch, a breakfast, and one free-text
        // note — enough to exercise fulfillment/cost roll-ups, the use-soon badge, and insights.
        // Day 0 breakfast: only the last member eats (override differs from the breakfast slot default).
        AssignRecipe(0, "Breakfast", "Greek Yogurt Overnight Oats", 2, attendeesOverride: kidsOnlyOverride);
        AssignRecipe(0, "Dinner", "Chicken & Chickpea Curry", 4);
        AssignRecipe(1, "Dinner", "Beef Chilli con Carne", 4);
        AssignRecipe(2, "Dinner", "Salmon & Vegetable Traybake", 2);
        AssignRecipe(3, "Lunch", "Chickpea & Spinach Stew", 4);
        AssignRecipe(4, "Dinner", "Chicken & Chickpea Curry", 4); // intentional repeat → "planned twice" insight

        if (Slot("Dinner") is { } fri)
            plan.AssignNote(monday.AddDays(5), fri.Id, "Takeout", attendeesOverride: null, source: "manual", createdBy: userId, clock);

        // Days 6 (Sunday) has no meals — leaves at least one open slot for the "N slots open" insight.
        // The current seed fills 7 of the 21 weekly cells (3 slots × 7 days), so emptyCells > 0 always.

        await mealPlanningDb.MealPlans.AddAsync(plan, ct);
        await mealPlanningDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seeds <see cref="PriceObservation"/> rows for the products used by the demo recipes so
    /// <see cref="PlanCostingService"/> can compute real cost figures. Prices are plausible UK
    /// supermarket values chosen so the seeded week totals roughly £60–80, which sits above a
    /// typical £50/week budget — useful once an over-budget insight is added.
    ///
    /// All prices are recorded in each product's default unit (matching <c>ProductTemplates</c>)
    /// so the costing unit conversion is always a trivial same-unit identity.
    /// </summary>
    private async Task SeedPriceObservationsAsync(HouseholdId householdId, Guid userId, CancellationToken ct)
    {
        // Load only leaf products (variants/parents are excluded from costing).
        var products = await catalogDb.Products
            .Where(p => !p.HasVariants)
            .ToDictionaryAsync(p => p.Name, ct);

        var now = clock.UtcNow;
        // Single synthetic sourceRef groups all observations as one "demo purchase" event.
        var sourceRef = Guid.CreateVersion7();

        // (product name, price-per-default-unit £)
        // Unit is the product's DefaultUnitId — prices are per one unit of the default measurement,
        // so the costing calculation is always a trivial same-unit identity (no conversion needed).
        //
        // Dairy & Eggs default unit is "l"; Condiments default unit is "ml"; everything else is "g".
        var pricePerUnit = new Dictionary<string, decimal>
        {
            // Dairy & Eggs (default unit = l)
            ["Whole milk"]       = 0.99m,    // £0.99 / l
            ["Greek yogurt"]     = 2.50m,    // £2.50 / l  (≈ £1.25 per 500 g tub, dense)
            // Meat & Fish (default unit = g)
            ["Chicken breast"]   = 0.0075m,  // £4.50 / 600 g = £0.0075/g
            ["Beef mince"]       = 0.008m,   // £4.00 / 500 g = £0.008/g
            ["Salmon fillet"]    = 0.0196m,  // £5.50 / 280 g ≈ £0.0196/g
            // Fruits & Veg (default unit = g)
            ["Onions"]           = 0.00138m, // £0.69 / 500 g
            ["Garlic"]           = 0.00865m, // £0.45 / 52 g
            ["Spinach"]          = 0.0055m,  // £1.10 / 200 g
            ["Broccoli"]         = 0.00198m, // £0.79 / 400 g
            ["Potatoes"]         = 0.00149m, // £1.49 / 1000 g
            ["Bell peppers"]     = 0.0043m,  // £1.29 / 300 g
            ["Lemons"]           = 0.0059m,  // £0.59 / 100 g
            // Frozen (default unit = g)
            ["Frozen berries"]   = 0.005m,   // £2.50 / 500 g
            // Pantry Staples (default unit = g)
            ["Rolled oats"]      = 0.0016m,  // £1.60 / 1000 g
            // Canned & Jarred (default unit = g)
            ["Chopped tomatoes"] = 0.001375m,// £0.55 / 400 g
            ["Coconut milk"]     = 0.002475m,// £0.99 / 400 g
            ["Chickpeas"]        = 0.001725m,// £0.69 / 400 g
            ["Red kidney beans"] = 0.001625m,// £0.65 / 400 g
            // Condiments (default unit = ml)
            ["Olive oil"]        = 0.00798m, // £3.99 / 500 ml
            // Herbs & Spices (default unit = g)
            ["Cumin — ground"]   = 0.03947m, // £1.50 / 38 g
            ["Paprika — smoked"] = 0.04571m, // £1.60 / 35 g
            ["Cayenne pepper"]   = 0.03947m, // £1.50 / 38 g
            ["Sea salt"]         = 0.00119m, // £0.89 / 750 g
            ["Black pepper"]     = 0.04286m, // £1.20 / 28 g
        };

        var observations = new List<PriceObservation>(pricePerUnit.Count);

        foreach (var (name, unitPrice) in pricePerUnit)
        {
            if (!products.TryGetValue(name, out var product)) continue;
            var unitId = product.DefaultUnitId.Value;

            // quantity = 1 default-unit of this product; price = unit price.
            // UnitPrice is supplied directly (already the per-base-unit price for g, ml, ea;
            // for l the UnitPriceCalculator would compute price / (qty × 1000) but we store
            // the pre-computed figure for clarity and unit-test reproducibility).
            observations.Add(PriceObservation.Record(
                householdId,
                product.Id.Value,
                skuId: null,
                price: unitPrice,          // total price for quantity = 1 default-unit
                quantity: 1m,              // 1 unit of product.DefaultUnitId
                unitId,
                unitPrice,                 // pre-computed (trivially price/1 = price)
                PriceSource.Purchase,
                merchantText: "Demo supermarket",
                sourceRef,
                observedAt: now.AddDays(-3),   // observed 3 days ago — recent but not today
                userId));
        }

        await pricingDb.PriceObservations.AddRangeAsync(observations, ct);
        await pricingDb.SaveChangesAsync(ct);
    }

    // ── Deals + flyer-review seeding (plantry-q9zr.14) ──────────────────────────────
    //
    // Replays a REAL, already-matched flyer export (superstore-flyer-2026-07.json, embedded) into two flyer
    // imports whose validity windows are rebased relative to IClock so /Deals/Review is deterministic and
    // never empties on flyer expiry. It never invokes the AI DealMatcher — the fixture IS the post-match
    // result — so it stays deterministic, key-free, and preserves the real 16-high/29-low/401-none tier
    // structure. See docs/Engineering/worktree-verification.md for how this pairs with the isolated-mode recipe.

    /// <summary>Postal code the demo store subscription is pulled for (a valid Canadian FSA for Flipp).</summary>
    internal const string DemoFlyerPostalCode = "K1A 0B1";

    /// <summary>Suffix distinguishing the prior-week import's dedup key from the current week's.</summary>
    internal const string PriorWeekExternalIdSuffix = "-prev";

    /// <summary>Every Nth suggestion-bearing prior-week deal is left expired-Pending instead of confirmed —
    /// so the DD14 tripwire covers a suggestion-bearing pending too, not only unmatched noise.</summary>
    private const int PriorWeekSuggestedPendingEvery = 8;

    /// <summary>How many unmatched prior-week noise rows the "reviewer" actively Rejected (the rest expire Pending).</summary>
    private const int PriorWeekRejectCount = 6;

    private async Task SeedDealsAsync(HouseholdId householdId, Guid userId, CancellationToken ct)
    {
        var fixture = LoadFixture();
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Store identity carrying the Flipp external ref (the dedup + subscription anchor).
        var store = Store.Create(householdId, fixture.Store, clock, externalRef: fixture.Flyer.ExternalId);
        await storeRepo.AddAsync(store, ct);
        await storeRepo.SaveChangesAsync(ct);
        var storeId = store.Id.Value;

        // Resolve every post-match suggested product name against the seeded demo catalog, creating any the
        // catalog doesn't ship (currently just "Fruit Snacks"). NEVER touches the AI matcher.
        var productIdByName = await ResolveSuggestedProductsAsync(householdId, fixture, ct);
        var hasSuggestion = fixture.Deals
            .Select(d => d.SuggestedProductName is { } n && productIdByName.ContainsKey(n))
            .ToList();

        // ── CURRENT week (today-2 → today+5): the full deal set, all Pending in the original tier mix. The
        // fixture's confirmed/rejected rows are seeded Pending here too (confidence preserved), so the review
        // queue shows every advertised deal — this is what the review-UX beads verify against. ──
        var (curFrom, curTo) = CurrentWeekWindow(today);
        var currentWindow = ValidityWindow.Create(curFrom, curTo).Value;
        await StageImportAsync(householdId, storeId, fixture.Flyer.ExternalId, currentWindow, fixture,
            productIdByName, fixture.Deals.Count, ct);

        // ── PRIOR week (today-9 → today-2, expired): a clone with a suffixed external id whose status pass
        // runs through the REAL domain verbs — the majority of resolvable (suggestion-bearing) deals Confirmed
        // (so DealConfirmedEvent fires and price observations land, giving the active list + price history real
        // data), a few Rejected, and a handful left Pending. Those expired-Pending deals must NOT surface in
        // the review queue (DD14) — the seed's permanent live tripwire for the queue filter. ──
        var (priorFrom, priorTo) = PriorWeekWindow(today);
        var priorWindow = ValidityWindow.Create(priorFrom, priorTo).Value;
        var actions = PlanPriorWeekActions(hasSuggestion);

        var priorDeals = await StageImportAsync(householdId, storeId,
            fixture.Flyer.ExternalId + PriorWeekExternalIdSuffix, priorWindow, fixture, productIdByName,
            actions.Count(a => a == PriorWeekAction.Pending), ct);

        for (var i = 0; i < priorDeals.Count; i++)
        {
            Result result;
            switch (actions[i])
            {
                case PriorWeekAction.Confirm:
                    result = await confirmDeal.ConfirmAsync(
                        priorDeals[i].Id, productIdByName[fixture.Deals[i].SuggestedProductName!], userId, ct);
                    break;
                case PriorWeekAction.Reject:
                    result = await rejectDeal.RejectAsync(priorDeals[i].Id, userId, rememberNegative: false, ct);
                    break;
                default:
                    continue; // Pending — left as staged (the expired DD14 tripwire).
            }

            if (result.IsFailure)
                throw new InvalidOperationException(
                    $"Seed prior-week deal verb failed for '{fixture.Deals[i].RawName}': " +
                    $"{result.Error.Code} — {result.Error.Description}");
        }

        // Standing subscription with a realistic post-ingest dedup anchor pointing at the current-week import.
        var subscription = StoreSubscription.Subscribe(householdId, storeId, DemoFlyerPostalCode, clock);
        subscription.RecordPull(fixture.Flyer.ExternalId, clock);
        dealsDb.StoreSubscriptions.Add(subscription);
        await dealsDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Inserts one Parsed <see cref="FlyerImport"/> and every fixture deal beneath it as <c>Pending</c>, in
    /// original flyer order. The import is saved before its deals because <c>deal → flyer_import</c> is a
    /// composite FK with no EF navigation (the same insert-ordering IngestFlyer relies on). The single shared
    /// <paramref name="window"/> instance legally backs both the import's and each deal's <c>valid_from/valid_to</c>
    /// columns — <see cref="ValidityWindow"/> is a complex type with value semantics (plantry-cegw). Returns the
    /// staged deals in fixture order so the caller can drive per-deal verbs.
    /// </summary>
    private async Task<IReadOnlyList<Deal>> StageImportAsync(
        HouseholdId householdId, Guid storeId, string externalId, ValidityWindow window,
        DealFixture fixture, IReadOnlyDictionary<string, Guid> productIdByName, int pendingCount, CancellationToken ct)
    {
        var import = FlyerImport.Start(
            householdId, storeId, externalId, contentHash: null, window, fixture.Flyer.RawFlyer.GetRawText(), clock);
        var marked = import.MarkParsed(pendingCount, clock);
        if (marked.IsFailure)
            throw new InvalidOperationException($"Seed flyer MarkParsed failed: {marked.Error.Description}");

        dealsDb.FlyerImports.Add(import);
        await dealsDb.SaveChangesAsync(ct); // INSERT the import before its deals (composite FK, no navigation)

        var deals = new List<Deal>(fixture.Deals.Count);
        foreach (var fd in fixture.Deals)
        {
            var productId = fd.SuggestedProductName is { } name && productIdByName.TryGetValue(name, out var pid)
                ? pid
                : (Guid?)null;

            // unit_id stays null: the export never resolved a pack unit (unitSymbol/quantity are absent), so
            // there is nothing to look up — matching the real ingest, which left these deals unit-less.
            var raw = new RawDeal(fd.RawName, fd.Brand, fd.Size, fd.Price, fd.Quantity, UnitId: null, fd.SaleStory, window);
            var normalized = new NormalizedName(fd.NormalizedName, DealNormalizer.NormalizerVersion);
            var proposal = new MatchProposal(productId, ParseConfidence(fd.Confidence), fd.Reasoning);

            var deal = Deal.Stage(householdId, import.Id, storeId, raw, normalized, proposal, clock);
            dealsDb.Deals.Add(deal);
            deals.Add(deal);
        }

        await dealsDb.SaveChangesAsync(ct);
        return deals;
    }

    /// <summary>
    /// Resolves the fixture's post-match suggested product names to catalog product ids, minting any name the
    /// demo catalog does not already ship (e.g. "Fruit Snacks") via the same <see cref="Product.Create"/> path
    /// the product seed uses. Returns a name→id map over the whole demo catalog.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Guid>> ResolveSuggestedProductsAsync(
        HouseholdId householdId, DealFixture fixture, CancellationToken ct)
    {
        var byName = await catalogDb.Products.ToDictionaryAsync(p => p.Name, p => p.Id.Value, ct);

        var missing = fixture.Deals
            .Select(d => d.SuggestedProductName)
            .Where(n => n is not null && !byName.ContainsKey(n))
            .Select(n => n!)
            .Distinct()
            .ToList();

        if (missing.Count == 0)
            return byName;

        var units = await catalogDb.Units.ToDictionaryAsync(u => u.Code, ct);
        var categories = await catalogDb.Categories.ToDictionaryAsync(c => c.Name, ct);
        if (!units.TryGetValue("g", out var grams))
            return byName; // reference data missing — leave those deals unmatched rather than fail the seed

        var created = new List<Product>();
        foreach (var name in missing)
        {
            var product = Product.Create(householdId, name, grams.Id, clock);
            if (categories.TryGetValue("Snacks", out var snacks))
                product.SetCategory(snacks.Id, clock);
            created.Add(product);
        }

        await catalogDb.Products.AddRangeAsync(created, ct);
        await catalogDb.SaveChangesAsync(ct);
        foreach (var p in created)
            byName[p.Name] = p.Id.Value;

        return byName;
    }

    // ── Pure, testable deal-seed helpers (mirrors the UntrackedStapleNames pattern) ──────────────────

    /// <summary>The current-week flyer window: opened 2 days ago, closing in 5 (an active, mid-run flyer).</summary>
    internal static (DateOnly ValidFrom, DateOnly ValidTo) CurrentWeekWindow(DateOnly today) =>
        (today.AddDays(-2), today.AddDays(5));

    /// <summary>The prior-week flyer window: closed 2 days ago (expired) — its Pending deals fall out of DD14.</summary>
    internal static (DateOnly ValidFrom, DateOnly ValidTo) PriorWeekWindow(DateOnly today) =>
        (today.AddDays(-9), today.AddDays(-2));

    internal enum PriorWeekAction { Confirm, Reject, Pending }

    /// <summary>
    /// Deterministic prior-week status pass over the deals in fixture order, given whether each has a resolved
    /// suggestion: confirm the majority of suggestion-bearing deals (leaving every
    /// <see cref="PriorWeekSuggestedPendingEvery"/>th expired-Pending), reject the first
    /// <see cref="PriorWeekRejectCount"/> unmatched noise rows, and leave every other deal Pending.
    /// </summary>
    internal static IReadOnlyList<PriorWeekAction> PlanPriorWeekActions(IReadOnlyList<bool> hasSuggestion)
    {
        var actions = new PriorWeekAction[hasSuggestion.Count];
        int suggestedSeen = 0, rejected = 0;
        for (var i = 0; i < hasSuggestion.Count; i++)
        {
            if (hasSuggestion[i])
            {
                actions[i] = suggestedSeen % PriorWeekSuggestedPendingEvery == PriorWeekSuggestedPendingEvery - 1
                    ? PriorWeekAction.Pending
                    : PriorWeekAction.Confirm;
                suggestedSeen++;
            }
            else if (rejected < PriorWeekRejectCount)
            {
                actions[i] = PriorWeekAction.Reject;
                rejected++;
            }
            else
            {
                actions[i] = PriorWeekAction.Pending;
            }
        }

        return actions;
    }

    private static MatchConfidence ParseConfidence(string value) =>
        Enum.Parse<MatchConfidence>(value, ignoreCase: true);

    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Loads + deserializes the embedded real-ingest flyer fixture from the Plantry.Web assembly.</summary>
    internal static DealFixture LoadFixture()
    {
        var assembly = typeof(FakeDataSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("superstore-flyer-2026-07.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Deal seed fixture 'superstore-flyer-2026-07.json' is not embedded in Plantry.Web.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded fixture stream '{resourceName}'.");

        return JsonSerializer.Deserialize<DealFixture>(stream, FixtureJsonOptions)
            ?? throw new InvalidOperationException("Deal seed fixture deserialized to null.");
    }

    // ── Fixture DTOs (shape of superstore-flyer-2026-07.json) ────────────────────────────────────────

    internal sealed record DealFixture(string Store, FixtureFlyer Flyer, IReadOnlyList<FixtureDeal> Deals);

    internal sealed record FixtureFlyer(string ExternalId, DateOnly ValidFrom, DateOnly ValidTo, JsonElement RawFlyer);

    internal sealed record FixtureDeal(
        string RawName,
        string? Brand,
        string? Size,
        decimal Price,
        decimal? Quantity,
        string? UnitSymbol,
        string? SaleStory,
        string NormalizedName,
        string Confidence,
        string? Reasoning,
        string? SuggestedProductName,
        string Status,
        bool AutoMatched);

    private async Task DeleteAllAsync(CancellationToken ct)
    {
        // Collect all household IDs before deleting anything, so we can arm tenant context
        // per-household to satisfy the strict catalog RLS policy (no carve-out there).
        var householdIds = await identityDb.Households
            .IgnoreQueryFilters()
            .Select(h => h.Id)
            .ToListAsync(ct);

        foreach (var hid in householdIds)
        {
            ArmTenant(hid.Value);
            try
            {
                // Inventory first (its rows soft-reference catalog products by Guid, so no FK forces
                // an order). Delete child-first to avoid the journal→stock_entry FK firing mid-cascade.
                await inventoryDb.StockJournalEntries.ExecuteDeleteAsync(ct);
                await inventoryDb.StockEntries.ExecuteDeleteAsync(ct);
                await inventoryDb.ProductStocks.ExecuteDeleteAsync(ct);

                // Meal plans + slot config soft-reference recipes/products by Guid (no FK), so order
                // among contexts is free. Aggregate-root cascades clear PlannedMeals/PlannedDishes and
                // MealSlots respectively.
                await mealPlanningDb.MealPlans.ExecuteDeleteAsync(ct);
                await mealPlanningDb.MealSlotConfigs.ExecuteDeleteAsync(ct);
                await mealPlanningDb.UserPreferences.ExecuteDeleteAsync(ct);
                await mealPlanningDb.TagStances.ExecuteDeleteAsync(ct);

                // Recipes (their ingredients soft-reference catalog products by Guid). Cook events
                // first — they FK → recipe; the recipe cascade then clears ingredients/tags/photo.
                await recipesDb.CookEvents.ExecuteDeleteAsync(ct);
                await recipesDb.Recipes.ExecuteDeleteAsync(ct);
                await recipesDb.Tags.ExecuteDeleteAsync(ct);

                // Price observations are append-only (no children) — delete before catalog so
                // there is no dangling product_id soft-ref concern.
                await pricingDb.PriceObservations.ExecuteDeleteAsync(ct);

                // Deals (plantry-q9zr.14): child deals FIRST — deal → flyer_import is a RESTRICT FK, so a
                // flyer_import cannot be deleted while its deals exist — then the flat subscription/memory
                // roots. All soft-reference catalog store/product by Guid (no FK), so ordering vs catalog is
                // free. ExecuteDelete honours the armed RLS query filter, staying within this household.
                await dealsDb.Deals.ExecuteDeleteAsync(ct);
                await dealsDb.FlyerImports.ExecuteDeleteAsync(ct);
                await dealsDb.DealMatchMemories.ExecuteDeleteAsync(ct);
                await dealsDb.StoreSubscriptions.ExecuteDeleteAsync(ct);

                // Product cascade FK handles product_skus and product_conversions.
                await catalogDb.Products.ExecuteDeleteAsync(ct);
                await catalogDb.Locations.ExecuteDeleteAsync(ct);
                await catalogDb.Categories.ExecuteDeleteAsync(ct);
                await catalogDb.Units.ExecuteDeleteAsync(ct);
                // Stores are minted by the deal seed (plantry-q9zr.14). The deals rows that soft-ref a store
                // by Guid are already deleted above, and nothing in catalog hard-FKs to Store, so ordering is
                // free — delete them here so /Dev/Reset does not accumulate an orphaned store row per reset.
                await catalogDb.Stores.ExecuteDeleteAsync(ct);
            }
            finally
            {
                DisarmTenant();
            }
        }

        // Identity tables: carve-out allows all rows visible with no tenant context.
        // User FK cascade handles AspNetUserClaims, AspNetUserTokens, etc.
        await identityDb.Users.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await identityDb.Households.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
    }

    // Arms both layers of tenant isolation that RlsMiddleware normally sets per-request.
    private void ArmTenant(Guid id)
    {
        tenant.Set(id);
        catalogDb.SetHouseholdId(id);
        identityDb.SetHouseholdId(id);
        inventoryDb.SetHouseholdId(id);
        recipesDb.SetHouseholdId(id);
        mealPlanningDb.SetHouseholdId(id);
        pricingDb.SetHouseholdId(id);
        dealsDb.SetHouseholdId(id);
    }

    private void DisarmTenant()
    {
        tenant.Clear();
        catalogDb.SetHouseholdId(Guid.Empty);
        identityDb.SetHouseholdId(Guid.Empty);
        inventoryDb.SetHouseholdId(Guid.Empty);
        recipesDb.SetHouseholdId(Guid.Empty);
        mealPlanningDb.SetHouseholdId(Guid.Empty);
        pricingDb.SetHouseholdId(Guid.Empty);
        dealsDb.SetHouseholdId(Guid.Empty);
    }

    // ── Static seed data ──────────────────────────────────────────────────────────

    private sealed record ProductTemplate(string[] Names, string DefaultUnit, string? DefaultLocation = null, int? DueDays = null);

    private static readonly Dictionary<string, ProductTemplate> ProductTemplates = new()
    {
        ["Dairy & Eggs"] = new(
            ["Whole milk", "Semi-skimmed milk", "Oat milk", "Almond milk", "Greek yogurt",
             "Plain yogurt", "Cheddar cheese", "Mozzarella", "Parmesan", "Butter",
             "Margarine", "Avocado Spread",
             "Cream cheese", "Free-range eggs", "Double cream", "Sour cream", "Brie"],
            DefaultUnit: "l", DefaultLocation: "Fridge", DueDays: 7),

        ["Meat & Fish"] = new(
            ["Chicken breast", "Chicken thighs", "Beef mince", "Pork mince", "Salmon fillet",
             "Tuna steak", "Cod fillet", "Bacon rashers", "Lamb chops", "Sausages",
             "Smoked salmon", "Prawns", "Sirloin steak", "Turkey mince"],
            DefaultUnit: "g", DefaultLocation: "Fridge", DueDays: 3),

        ["Fruits and Vegetables"] = new(
            ["Apples", "Bananas", "Oranges", "Lemons", "Avocados", "Tomatoes",
             "Spinach", "Kale", "Carrots", "Broccoli", "Onions", "Garlic",
             "Potatoes", "Sweet potatoes", "Bell peppers", "Courgettes", "Cucumber"],
            DefaultUnit: "g", DefaultLocation: "Fridge", DueDays: 5),

        ["Bread & Bakery"] = new(
            ["Wholemeal bread", "White bread", "Sourdough loaf", "Baguette", "Pita bread",
             "Bagels", "Croissants", "English muffins", "Ciabatta", "Rye bread"],
            DefaultUnit: "ea", DefaultLocation: "Pantry", DueDays: 4),

        ["Frozen"] = new(
            ["Frozen peas", "Frozen sweetcorn", "Frozen spinach", "Oven chips",
             "Ice cream — vanilla", "Frozen berries", "Fish fingers", "Frozen edamame",
             "Frozen prawns", "Pizza — margherita"],
            DefaultUnit: "g", DefaultLocation: "Freezer", DueDays: 90),

        ["Pantry Staples"] = new(
            ["Plain flour", "Self-raising flour", "Caster sugar", "Brown sugar",
             "Basmati rice", "Arborio rice", "Rolled oats", "Cornflour",
             "Breadcrumbs", "Baking powder", "Bicarbonate of soda", "Yeast"],
            DefaultUnit: "g", DefaultLocation: "Pantry"),

        ["Canned & Jarred"] = new(
            ["Chopped tomatoes", "Coconut milk", "Chickpeas", "Red kidney beans",
             "Lentils", "Tuna in brine", "Baked beans", "Tomato purée",
             "Chicken stock", "Vegetable stock", "Sweetcorn", "Black beans"],
            DefaultUnit: "g", DefaultLocation: "Pantry"),

        ["Drinks"] = new(
            ["Orange juice", "Apple juice", "Sparkling water", "Still water",
             "Green tea", "Earl grey tea", "Coffee — ground", "Coffee — instant",
             "Coconut water", "Lemonade"],
            DefaultUnit: "l", DefaultLocation: "Pantry"),

        ["Condiments"] = new(
            ["Olive oil", "Sunflower oil", "Soy sauce", "Worcestershire sauce",
             "Tomato ketchup", "Mayonnaise", "Mustard — Dijon", "Mustard — wholegrain",
             "White wine vinegar", "Balsamic vinegar", "Hot sauce", "Sriracha"],
            DefaultUnit: "ml", DefaultLocation: "Pantry"),

        ["Herbs and Spices"] = new(
            ["Cumin — ground", "Coriander — ground", "Turmeric", "Paprika — smoked",
             "Paprika — sweet", "Cinnamon — ground", "Chilli flakes", "Oregano",
             "Thyme", "Rosemary", "Bay leaves", "Black pepper", "Sea salt", "Cayenne pepper"],
            DefaultUnit: "g", DefaultLocation: "Pantry"),

        ["Snacks"] = new(
            ["Dark chocolate", "Milk chocolate", "Crackers", "Rice cakes",
             "Mixed nuts", "Cashews", "Almonds", "Popcorn",
             "Crisps — ready salted", "Granola bars", "Dried mango", "Trail mix"],
            DefaultUnit: "g", DefaultLocation: "Pantry"),
    };

    // Parent name + which variant names belong to it + which category to assign the parent.
    private static readonly (string Parent, string[] Variants, string Category)[] VariantGroupDefinitions =
    [
        ("Milk",  ["Whole milk", "Semi-skimmed milk", "Oat milk", "Almond milk"],              "Dairy & Eggs"),
        ("Bread", ["Wholemeal bread", "White bread", "Sourdough loaf", "Rye bread", "Baguette"], "Bread & Bakery"),
    ];

    // Derived: variant product name → its parent product name.
    private static readonly Dictionary<string, string> VariantGroupMap =
        VariantGroupDefinitions
            .SelectMany(g => g.Variants.Select(v => (v, g.Parent)))
            .ToDictionary(x => x.v, x => x.Parent);

    // SKUs: product name → (display label, size quantity, size unit code).
    private static readonly Dictionary<string, (string Label, decimal? Qty, string? UnitCode)[]> SkuTemplates = new()
    {
        ["Whole milk"]        = [("1L", 1m, "l"), ("2L", 2m, "l"), ("4-pint", null, null)],
        ["Semi-skimmed milk"] = [("1L", 1m, "l"), ("2L", 2m, "l")],
        ["Oat milk"]          = [("1L", 1m, "l"), ("1.75L", 1.75m, "l")],
        ["Almond milk"]       = [("1L", 1m, "l")],
        ["Cheddar cheese"]    = [("200g", 200m, "g"), ("400g", 400m, "g"), ("1kg block", 1000m, "g")],
        ["Greek yogurt"]      = [("150g pot", 150m, "g"), ("500g tub", 500m, "g")],
        ["Basmati rice"]      = [("500g", 500m, "g"), ("1kg", 1000m, "g"), ("2kg", 2000m, "g")],
        ["Olive oil"]         = [("250ml", 250m, "ml"), ("500ml", 500m, "ml"), ("1L", 1000m, "ml")],
        ["Salmon fillet"]     = [("2-pack (280g)", 280m, "g"), ("4-pack (560g)", 560m, "g")],
        ["Chicken breast"]    = [("2-pack (400g)", 400m, "g"), ("4-pack (800g)", 800m, "g")],
    };

    // Conversions: product name → (fromUnitCode, toUnitCode, factor) where 1 from = factor × to.
    // Mostly cross-dimension (volume → mass) since those can't be derived from the standard unit table.
    private static readonly Dictionary<string, (string From, string To, decimal Factor)[]> ConversionTemplates = new()
    {
        ["Plain flour"]  = [("cup", "g", 120m)],   // 1 cup plain flour ≈ 120 g
        ["Caster sugar"] = [("cup", "g", 200m)],   // 1 cup caster sugar ≈ 200 g
        ["Basmati rice"] = [("cup", "g", 185m)],   // 1 cup basmati rice ≈ 185 g
        ["Arborio rice"] = [("cup", "g", 205m)],   // 1 cup arborio rice ≈ 205 g
        ["Rolled oats"]  = [("cup", "g", 90m)],    // 1 cup rolled oats ≈ 90 g
        ["Butter"]       = [("tbsp", "g", 14m)],   // 1 tbsp butter ≈ 14 g
    };

    // ── Recipe seed data ──────────────────────────────────────────────────────────
    // A handful of demo recipes spanning the seeded Diet/Protein/Flavor tags. Every product
    // name below must exist in ProductTemplates above (recipes whose ingredients aren't all
    // seeded are skipped). Qty is in the product's default unit; a null Qty is a "to taste" line.

    private sealed record RecipeSeed(
        string Name,
        string? Source,
        int? CookTimeMinutes,
        int Servings,
        string[] Tags,
        (string Product, decimal? Qty, string? Group)[] Lines,
        string Directions);

    private static readonly RecipeSeed[] RecipeSeeds =
    [
        new("Chicken & Chickpea Curry", "Plantry kitchen", 40, 4,
            ["Poultry", "Spicy"],
            [
                ("Chicken breast",   600m, null),
                ("Onions",           200m, null),
                ("Garlic",            15m, null),
                ("Chopped tomatoes", 400m, null),
                ("Coconut milk",     400m, null),
                ("Chickpeas",        400m, null),
                ("Cumin — ground",    10m, null),
                ("Paprika — smoked",   5m, null),
                ("Olive oil",         30m, null),
                ("Sea salt",        null, null),
            ],
            """
            Heat the olive oil in a large pan and soften the diced onions for 5 minutes.

            Stir in the garlic, cumin and smoked paprika and cook until fragrant.

            Add the diced chicken and brown all over.

            Pour in the chopped tomatoes and coconut milk, then add the drained chickpeas.

            Simmer for 25 minutes until the sauce thickens and the chicken is cooked through.

            Season to taste and serve with rice.
            """),

        new("Beef Chilli con Carne", null, 50, 4,
            ["Meat", "Spicy"],
            [
                ("Beef mince",        500m, null),
                ("Onions",            150m, null),
                ("Garlic",             10m, null),
                ("Bell peppers",      150m, null),
                ("Chopped tomatoes",  400m, null),
                ("Red kidney beans",  400m, null),
                ("Cumin — ground",     10m, null),
                ("Cayenne pepper",      3m, null),
                ("Olive oil",          30m, null),
                ("Sea salt",         null, null),
            ],
            """
            Brown the beef mince in the olive oil, breaking it up as it cooks, then set aside.

            Soften the onions and peppers in the same pan, then add the garlic, cumin and cayenne.

            Return the mince, stir in the chopped tomatoes and kidney beans.

            Simmer gently for 35 minutes, stirring occasionally, until rich and thick.

            Season to taste and serve.
            """),

        new("Salmon & Vegetable Traybake", null, 35, 2,
            ["Fish"],
            [
                ("Salmon fillet", 280m, "For the traybake"),
                ("Potatoes",      400m, "For the traybake"),
                ("Broccoli",      200m, "For the traybake"),
                ("Bell peppers",  150m, "For the traybake"),
                ("Olive oil",      30m, "For the traybake"),
                ("Lemons",        100m, "To finish"),
                ("Black pepper", null, "To finish"),
                ("Sea salt",     null, "To finish"),
            ],
            """
            # Roast the vegetables
            Heat the oven to 200°C. Toss the chopped potatoes and peppers in half the olive oil and roast for 15 minutes.

            # Add salmon and finish
            Add the broccoli and the salmon fillets, drizzle with the remaining oil.

            Roast for a further 15 minutes until the salmon flakes easily and the vegetables are tender.

            Finish with a squeeze of lemon and season to taste.
            """),

        new("Chickpea & Spinach Stew", "Weeknight vegan", 30, 4,
            ["Vegan", "Vegetarian", "Spicy"],
            [
                ("Chickpeas",        400m, null),
                ("Spinach",          200m, null),
                ("Onions",           150m, null),
                ("Garlic",            10m, null),
                ("Chopped tomatoes", 400m, null),
                ("Coconut milk",     400m, null),
                ("Cumin — ground",    10m, null),
                ("Olive oil",         30m, null),
                ("Sea salt",        null, null),
            ],
            """
            Soften the onions in the olive oil, then stir in the garlic and cumin.

            Add the chopped tomatoes, coconut milk and drained chickpeas and simmer for 15 minutes.

            Stir through the spinach a handful at a time until wilted.

            Season to taste and serve.
            """),

        new("Greek Yogurt Overnight Oats", null, 5, 1,
            ["Vegetarian"],
            [
                ("Rolled oats",     50m, null),
                ("Whole milk",     0.2m, null),
                ("Greek yogurt",  0.15m, null),
                ("Frozen berries",  80m, null),
            ],
            """
            Stir the oats, milk and yogurt together in a jar or bowl.

            Top with the frozen berries, cover and refrigerate overnight.

            Eat straight from the fridge in the morning.
            """),
    ];

    // ── Derived untracked-staple set + fail-loud seed guard ─────────────────────────

    /// <summary>
    /// Product names that appear in <see cref="RecipeSeeds"/> ONLY as null-quantity ("to taste")
    /// lines. These are untracked staples: minting them tracked would produce seed recipes that
    /// <c>AuthorRecipe</c>'s R5 rule rejects on save (a tracked ingredient needs both quantity and
    /// unit). Derived from usage — not a hard-coded name list — so new seed recipes stay
    /// self-consistent. On the current seed data this set is exactly {Sea salt, Black pepper}.
    /// A product used with a real quantity anywhere is excluded (stays tracked), so there is never
    /// a both-ways conflict. Declared after <see cref="RecipeSeeds"/> so the static field it reads
    /// is already initialised.
    /// </summary>
    internal static readonly IReadOnlySet<string> UntrackedStapleNames =
        RecipeSeeds
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.Product)
            .Where(g => g.All(l => l.Qty is null))
            .Select(g => g.Key)
            .ToHashSet();

    /// <summary>
    /// Fail-loud seed guard for the R5 invariant. A tracked product paired with a null ("to taste")
    /// quantity would yield a recipe that <c>AuthorRecipe</c> rejects on save; asserting at seed time
    /// makes a mis-seeded staple fail fast, naming the offending product and recipe.
    /// </summary>
    internal static void AssertSeedLineSatisfiesR5(Product product, decimal? quantity, string recipeName)
    {
        if (product.TrackStock && quantity is null)
            throw new InvalidOperationException(
                $"Seed data violates R5: tracked product '{product.Name}' has a null (\"to taste\") " +
                $"quantity in recipe '{recipeName}'. Mint it with trackStock:false (an untracked " +
                $"staple, see UntrackedStapleNames) or give the seed line a quantity.");
    }
}
