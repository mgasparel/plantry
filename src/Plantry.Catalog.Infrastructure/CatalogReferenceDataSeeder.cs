using Plantry.Catalog.Domain;
using Plantry.Identity.Domain;
using Plantry.SharedKernel;

namespace Plantry.Catalog.Infrastructure;

/// <summary>
/// Implements IReferenceDataSeeder for the Catalog context.
/// Seeds standard units, starter categories, and starter locations on household creation (DM-9).
/// </summary>
public sealed class CatalogReferenceDataSeeder(CatalogDbContext db) : IReferenceDataSeeder
{
    public async Task SeedAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var units = BuildUnits(householdId);
        var categories = BuildCategories(householdId);
        var locations = BuildLocations(householdId);

        await db.Units.AddRangeAsync(units, ct);
        await db.Categories.AddRangeAsync(categories, ct);
        await db.Locations.AddRangeAsync(locations, ct);
        await db.SaveChangesAsync(ct);
    }

    private static List<Unit> BuildUnits(HouseholdId hid)
    {
        // Mass units (base: gram)
        var gram      = Unit.Create(hid, "g",   "gram",      Dimension.Mass,   1m,       isBase: true);
        var kilogram  = Unit.Create(hid, "kg",  "kilogram",  Dimension.Mass,   1000m);
        var milligram = Unit.Create(hid, "mg",  "milligram", Dimension.Mass,   0.001m);
        var ounce     = Unit.Create(hid, "oz",  "ounce",     Dimension.Mass,   28.3495m);
        var pound     = Unit.Create(hid, "lb",  "pound",     Dimension.Mass,   453.592m);

        // Volume units (base: millilitre)
        var ml    = Unit.Create(hid, "ml",    "millilitre", Dimension.Volume, 1m,    isBase: true);
        var litre = Unit.Create(hid, "l",     "litre",      Dimension.Volume, 1000m);
        var flOz  = Unit.Create(hid, "fl oz", "fl oz",      Dimension.Volume, 29.5735m);
        var cup   = Unit.Create(hid, "cup",   "cup",        Dimension.Volume, 240m);
        var tsp   = Unit.Create(hid, "tsp",   "teaspoon",   Dimension.Volume, 4.92892m);
        var tbsp  = Unit.Create(hid, "tbsp",  "tablespoon", Dimension.Volume, 14.7868m);

        // Count units (base: each)
        var each  = Unit.Create(hid, "ea",  "each",  Dimension.Count, 1m, isBase: true);
        var pack  = Unit.Create(hid, "pk",  "pack",  Dimension.Count, 1m);
        var dozen = Unit.Create(hid, "doz", "dozen", Dimension.Count, 12m);

        return [gram, kilogram, milligram, ounce, pound,
                ml, litre, flOz, cup, tsp, tbsp,
                each, pack, dozen];
    }

    private static List<Category> BuildCategories(HouseholdId hid)
    {
        int i = 0;
        Category Cat(string name, int? days = null, int? hue = null) =>
            Category.Create(hid, name, days, sortOrder: i++ * 10, hue: hue);

        return [
            Cat("Dairy & Eggs",          7,  hue: 210),
            Cat("Meat & Fish",           3,  hue: 10),
            Cat("Fruits and Vegetables", 5,  hue: 145),
            Cat("Bread & Bakery",        4,  hue: 30),
            Cat("Deli",                  5,  hue: 45),
            Cat("Frozen",                90, hue: 200),
            Cat("Pantry Staples",           hue: 60),
            Cat("Canned & Jarred",          hue: 25),
            Cat("Drinks",                   hue: 240),
            Cat("Condiments",               hue: 80),
            Cat("Herbs and Spices",         hue: 120),
            Cat("Snacks",                   hue: 350),
            Cat("Other",                    hue: 270),
        ];
    }

    private static List<Location> BuildLocations(HouseholdId hid) =>
    [
        Location.Create(hid, "Fridge",  LocationType.Ambient),
        Location.Create(hid, "Freezer", LocationType.Frozen),
        Location.Create(hid, "Pantry",  LocationType.Ambient),
        Location.Create(hid, "Counter", LocationType.Ambient),
    ];
}
