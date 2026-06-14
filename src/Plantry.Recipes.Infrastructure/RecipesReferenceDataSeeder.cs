using Plantry.Identity.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// Implements IReferenceDataSeeder for the Recipes context.
/// Seeds the eight default tags on household creation (DM-9), mirroring the units/categories/locations
/// the Catalog context seeds. Three of the four <see cref="TagCategory"/> values get starter tags;
/// Cuisine ships with none — those are minted inline from the editor (recipes-domain-model.md §5).
/// </summary>
public sealed class RecipesReferenceDataSeeder(RecipesDbContext db, IClock clock) : IReferenceDataSeeder
{
    public async Task SeedAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var tags = BuildTags(householdId);

        await db.Tags.AddRangeAsync(tags, ct);
        await db.SaveChangesAsync(ct);
    }

    private List<Tag> BuildTags(HouseholdId hid) =>
    [
        // Diet
        Tag.Create(hid, "Vegetarian",  TagCategory.Diet,    clock),
        Tag.Create(hid, "Vegan",       TagCategory.Diet,    clock),
        Tag.Create(hid, "Dairy-Free",  TagCategory.Diet,    clock),
        Tag.Create(hid, "Gluten-Free", TagCategory.Diet,    clock),

        // Protein
        Tag.Create(hid, "Meat",        TagCategory.Protein, clock),
        Tag.Create(hid, "Poultry",     TagCategory.Protein, clock),
        Tag.Create(hid, "Fish",        TagCategory.Protein, clock),

        // Flavor
        Tag.Create(hid, "Spicy",       TagCategory.Flavor,  clock),
    ];
}
