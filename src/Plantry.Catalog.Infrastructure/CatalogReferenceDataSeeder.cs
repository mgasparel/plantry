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
        var hid = householdId.Value;

        var units = BuildUnits(hid);
        var categories = BuildCategories(hid);
        var locations = BuildLocations(hid);

        await db.Units.AddRangeAsync(units, ct);
        await db.Categories.AddRangeAsync(categories, ct);
        await db.Locations.AddRangeAsync(locations, ct);
        await db.SaveChangesAsync(ct);
    }

    private static List<UnitRow> BuildUnits(Guid hid)
    {
        // Mass units (base: gram)
        var gram      = Unit(hid, "gram",      "g",   "mass",   1m,       isBase: true);
        var kilogram  = Unit(hid, "kilogram",  "kg",  "mass",   1000m);
        var milligram = Unit(hid, "milligram", "mg",  "mass",   0.001m);
        var ounce     = Unit(hid, "ounce",     "oz",  "mass",   28.3495m);
        var pound     = Unit(hid, "pound",     "lb",  "mass",   453.592m);

        // Volume units (base: millilitre)
        var ml   = Unit(hid, "millilitre", "ml",  "volume", 1m,    isBase: true);
        var litre = Unit(hid, "litre",     "l",   "volume", 1000m);
        var flOz  = Unit(hid, "fl oz",     "fl oz", "volume", 29.5735m);
        var cup   = Unit(hid, "cup",       "cup", "volume", 240m);
        var tsp   = Unit(hid, "teaspoon",  "tsp", "volume", 4.92892m);
        var tbsp  = Unit(hid, "tablespoon","tbsp","volume", 14.7868m);

        // Count units (base: each)
        var each  = Unit(hid, "each",  "ea",  "count", 1m, isBase: true);
        var pack  = Unit(hid, "pack",  "pk",  "count", 1m);
        var dozen = Unit(hid, "dozen", "doz", "count", 12m);

        return [gram, kilogram, milligram, ounce, pound,
                ml, litre, flOz, cup, tsp, tbsp,
                each, pack, dozen];
    }

    private static List<CategoryRow> BuildCategories(Guid hid)
    {
        int i = 0;
        CategoryRow Cat(string name, int? days = null) =>
            new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = name,
                    DefaultDueDays = days, SortOrder = i++ * 10 };

        return [
            Cat("Dairy & Eggs",    7),
            Cat("Meat & Fish",     3),
            Cat("Fruit",           5),
            Cat("Vegetables",      7),
            Cat("Bread & Bakery",  4),
            Cat("Deli",            5),
            Cat("Frozen",          90),
            Cat("Pantry Staples"),
            Cat("Canned & Jarred"),
            Cat("Drinks"),
            Cat("Condiments"),
            Cat("Snacks"),
            Cat("Baby & Toddler"),
            Cat("Other"),
        ];
    }

    private static List<LocationRow> BuildLocations(Guid hid) =>
    [
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = "Fridge",    LocationType = "ambient" },
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = "Freezer",   LocationType = "frozen"  },
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = "Pantry",    LocationType = "ambient" },
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = "Counter",   LocationType = "ambient" },
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = "Wine Rack", LocationType = "ambient" },
    ];

    private static UnitRow Unit(Guid hid, string name, string symbol,
        string dimension, decimal factor, bool isBase = false) =>
        new() { Id = Guid.CreateVersion7(), HouseholdId = hid, Name = name,
                Symbol = symbol, Dimension = dimension,
                FactorToBase = factor, IsBase = isBase };
}
