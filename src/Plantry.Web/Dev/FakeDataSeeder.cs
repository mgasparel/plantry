using System.Security.Claims;
using Bogus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Inventory.Domain;
using Plantry.Inventory.Infrastructure;
using Plantry.MealPlanning.Domain;
using Plantry.MealPlanning.Infrastructure;
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
            // Meal plan must seed after recipes (references their ids) and after the slot config that
            // RegisterHouseholdCommand created (Breakfast/Lunch/Dinner) — gives the planner a populated
            // current week on first load instead of an empty grid.
            await SeedMealPlanAsync(householdId, userId, ct);
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

        await recipesDb.Recipes.AddRangeAsync(recipes, ct);
        await recipesDb.SaveChangesAsync(ct);
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
                var product = Product.Create(householdId, name, unit.Id, clock);
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

    private async Task SeedMealPlanAsync(HouseholdId householdId, Guid userId, CancellationToken ct)
    {
        // The slot config (Breakfast/Lunch/Dinner) was created by MealPlanningReferenceDataSeeder
        // during RegisterHouseholdCommand. Load it to resolve slot ids by label.
        var config = await mealPlanningDb.MealSlotConfigs
            .Include(c => c.Slots)
            .FirstOrDefaultAsync(ct);
        if (config is null) return;

        MealSlot? Slot(string label) =>
            config.Slots.FirstOrDefault(s => s.Label == label && s.IsActive);

        // Recipes seeded above, resolved by name.
        var recipes = await recipesDb.Recipes.ToDictionaryAsync(r => r.Name, ct);

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var monday = MealPlan.NormalizeToMonday(today);
        var plan = MealPlan.Start(householdId, monday, clock);

        void AssignRecipe(int dayOffset, string slotLabel, string recipeName, int servings)
        {
            if (!recipes.TryGetValue(recipeName, out var recipe)) return;
            if (Slot(slotLabel) is not { } slot) return;
            plan.AssignMeal(
                monday.AddDays(dayOffset), slot.Id,
                [new DishSpec(DishKind.Recipe, recipe.Id.Value, servings)],
                attendeesOverride: null, source: "manual", createdBy: userId, clock);
        }

        // A representative half-full week: a mix of dinners, a lunch, a breakfast, and one free-text
        // note — enough to exercise fulfillment/cost roll-ups, the use-soon badge, and insights.
        AssignRecipe(0, "Breakfast", "Greek Yogurt Overnight Oats", 2);
        AssignRecipe(0, "Dinner", "Chicken & Chickpea Curry", 4);
        AssignRecipe(1, "Dinner", "Beef Chilli con Carne", 4);
        AssignRecipe(2, "Dinner", "Salmon & Vegetable Traybake", 2);
        AssignRecipe(3, "Lunch", "Chickpea & Spinach Stew", 4);
        AssignRecipe(4, "Dinner", "Chicken & Chickpea Curry", 4); // intentional repeat → "planned twice" insight

        if (Slot("Dinner") is { } fri)
            plan.AssignNote(monday.AddDays(5), fri.Id, "Takeout", attendeesOverride: null, source: "manual", createdBy: userId, clock);

        await mealPlanningDb.MealPlans.AddAsync(plan, ct);
        await mealPlanningDb.SaveChangesAsync(ct);
    }

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

                // Product cascade FK handles product_skus and product_conversions.
                await catalogDb.Products.ExecuteDeleteAsync(ct);
                await catalogDb.Locations.ExecuteDeleteAsync(ct);
                await catalogDb.Categories.ExecuteDeleteAsync(ct);
                await catalogDb.Units.ExecuteDeleteAsync(ct);
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
    }

    private void DisarmTenant()
    {
        tenant.Clear();
        catalogDb.SetHouseholdId(Guid.Empty);
        identityDb.SetHouseholdId(Guid.Empty);
        inventoryDb.SetHouseholdId(Guid.Empty);
        recipesDb.SetHouseholdId(Guid.Empty);
        mealPlanningDb.SetHouseholdId(Guid.Empty);
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
}
