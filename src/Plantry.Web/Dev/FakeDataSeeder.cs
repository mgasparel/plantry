using System.Security.Claims;
using Bogus;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Plantry.Catalog.Domain;
using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Application;
using Plantry.Identity.Domain;
using Plantry.Identity.Infrastructure;
using Plantry.Inventory.Infrastructure;
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
            new Faker("en") { Random = new Randomizer(42) }, ct);

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
        string householdName, string email, string password, Faker faker, CancellationToken ct)
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

        // Arm both the Postgres GUC (via TenantContext → interceptor) and the EF query filter
        // (via SetHouseholdId), mirroring what RlsMiddleware does for authenticated requests.
        ArmTenant(householdId.Value);
        try
        {
            await SeedProductsAsync(householdId, faker, ct);
        }
        finally
        {
            DisarmTenant();
        }
    }

    private async Task SeedProductsAsync(HouseholdId householdId, Faker faker, CancellationToken ct)
    {
        var units = await catalogDb.Units.ToDictionaryAsync(u => u.Code, ct);
        var categories = await catalogDb.Categories.ToDictionaryAsync(c => c.Name, ct);
        var locations = await catalogDb.Locations.ToDictionaryAsync(l => l.Name, ct);

        // Build the flat product list first (SKUs + conversions, no variant links yet).
        var allProducts = BuildProducts(householdId, units, categories, locations, faker);
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

    private List<Product> BuildProducts(
        HouseholdId householdId,
        Dictionary<string, Unit> units,
        Dictionary<string, Category> categories,
        Dictionary<string, Location> locations,
        Faker faker)
    {
        var products = new List<Product>();

        foreach (var (categoryName, tmpl) in ProductTemplates)
        {
            if (!categories.TryGetValue(categoryName, out var cat)) continue;
            if (!units.TryGetValue(tmpl.DefaultUnit, out var unit)) continue;

            var count = Math.Min(tmpl.Names.Length, faker.Random.Int(3, 6));
            foreach (var name in faker.Random.ArrayElements(tmpl.Names, count))
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
    }

    private void DisarmTenant()
    {
        tenant.Clear();
        catalogDb.SetHouseholdId(Guid.Empty);
        identityDb.SetHouseholdId(Guid.Empty);
        inventoryDb.SetHouseholdId(Guid.Empty);
    }

    // ── Static seed data ──────────────────────────────────────────────────────────

    private sealed record ProductTemplate(string[] Names, string DefaultUnit, string? DefaultLocation = null, int? DueDays = null);

    private static readonly Dictionary<string, ProductTemplate> ProductTemplates = new()
    {
        ["Dairy & Eggs"] = new(
            ["Whole milk", "Semi-skimmed milk", "Oat milk", "Almond milk", "Greek yogurt",
             "Plain yogurt", "Cheddar cheese", "Mozzarella", "Parmesan", "Butter",
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
}
