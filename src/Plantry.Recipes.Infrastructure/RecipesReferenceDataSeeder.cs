using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plantry.Identity.Domain;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Recipes.Infrastructure;

/// <summary>
/// Implements IReferenceDataSeeder for the Recipes context.
/// Seeds the ten default tags on household creation (DM-9), mirroring the units/categories/locations
/// the Catalog context seeds. Three of the four <see cref="TagCategory"/> values get starter tags;
/// Cuisine ships with none — those are minted inline from the editor (recipes-domain-model.md §5).
/// </summary>
public sealed class RecipesReferenceDataSeeder(RecipesDbContext db, IClock clock) : IReferenceDataSeeder
{
    private static readonly (string Name, TagCategory Category)[] PlantProteinVocabulary =
    [
        ("Tofu", TagCategory.Protein),
        ("Legumes", TagCategory.Protein),
    ];

    public async Task SeedAsync(HouseholdId householdId, CancellationToken ct = default)
    {
        var tags = BuildTags(householdId);

        await db.Tags.AddRangeAsync(tags, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds the plant-protein vocabulary introduced after the original eight-tag seed set. Existing
    /// vocabulary is authoritative: matching names are left unchanged even when archived or assigned a
    /// different category. This makes the rollout safe to repeat without rewriting user-owned tags.
    /// </summary>
    public async Task<int> SeedMissingPlantProteinVocabularyAsync(
        HouseholdId householdId,
        CancellationToken ct = default)
    {
        var rolloutNames = PlantProteinVocabulary.Select(item => item.Name.ToLowerInvariant()).ToList();
        var existingNames = await db.Tags
            .Where(tag => tag.HouseholdId == householdId && rolloutNames.Contains(tag.Name.ToLower()))
            .Select(tag => tag.Name)
            .ToListAsync(ct);
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = PlantProteinVocabulary
            .Where(item => !existing.Contains(item.Name))
            .Select(item => Tag.Create(householdId, item.Name, item.Category, clock))
            .ToList();

        if (missing.Count == 0) return 0;

        await db.Tags.AddRangeAsync(missing, ct);
        await db.SaveChangesAsync(ct);
        return missing.Count;
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
        // Plant-protein vocabulary is semantic reference data only. It does not apply a Diet stance or
        // assume how the household eats; recipes still receive these tags only through user confirmation.
        .. PlantProteinVocabulary.Select(item => Tag.Create(hid, item.Name, item.Category, clock)),

        // Flavor
        Tag.Create(hid, "Spicy",       TagCategory.Flavor,  clock),
    ];
}

/// <summary>
/// Reconciles the plant-protein vocabulary for every household after the ten-tag seed set ships. The
/// enumeration scope is deliberately unarmed so <see cref="IHouseholdRepository.ListAllIdsAsync"/> can
/// use its RLS carve-out; each household then runs in a fresh scope with both Postgres RLS and the Recipes
/// EF query filter armed, mirroring <c>RecipeConversionBackfillCycle</c>.
/// </summary>
public sealed class RecipesReferenceDataRollout(
    IServiceScopeFactory scopeFactory,
    ILogger<RecipesReferenceDataRollout> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HouseholdId> households;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            households = await scope.ServiceProvider
                    .GetRequiredService<IHouseholdRepository>()
                    .ListAllIdsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recipe vocabulary rollout could not enumerate households.");
            return;
        }

        var added = 0;
        foreach (var household in households)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                added += await RunForHouseholdAsync(household, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Recipe vocabulary rollout failed for household {HouseholdId}; continuing to the next.",
                    household.Value);
            }
        }

        logger.LogInformation(
            "Recipe vocabulary rollout completed for {HouseholdCount} household(s); {TagCount} tag(s) added.",
            households.Count,
            added);
    }

    public async Task<int> RunForHouseholdAsync(HouseholdId household, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var householdId = household.Value;

        services.GetRequiredService<TenantContext>().Set(householdId);
        services.GetRequiredService<RecipesDbContext>().SetHouseholdId(householdId);

        return await services.GetRequiredService<RecipesReferenceDataSeeder>()
            .SeedMissingPlantProteinVocabularyAsync(household, ct);
    }
}
