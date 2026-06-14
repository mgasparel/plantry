using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using RecipesDomain = Plantry.Recipes.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Builds a deterministic <see cref="Recipe"/> for L4 snapshot tests of the Detail page.
/// The recipe exercises all render paths: a photo (present so the image link renders), meta
/// (servings, cook time, source), tag pills, an ingredient list with group headings and an
/// untracked staple (C12), and multi-paragraph directions with a section heading (C13).
/// </summary>
public static class RecipeDetailFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // Fixed recipe id (scrubbed to Guid_N in snapshots).
    public static readonly RecipeId RecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    // Product ids.
    public static readonly Guid TomatoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GarlicId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PastaId  = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid SaltId   = Guid.Parse("44444444-4444-4444-4444-444444444444"); // untracked

    public static readonly Guid EachUnitId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid GramUnitId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // Tag ids.
    public static readonly TagId VegetarianTagId = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    public static readonly TagId SpicyTagId      = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));

    /// <summary>
    /// Builds the representative recipe via the domain API. Photo is set to a minimal JPEG
    /// stub so <see cref="Recipe.Photo"/> is non-null (the Detail page renders an img tag).
    /// </summary>
    public static Recipe Build()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = SystemClock.Instance;

        var recipe = Recipe.Create(hid, "Tomato Pasta", defaultServings: 4, clock).Value;

        recipe.SetCookTime(30, clock);
        recipe.SetSource("Nonna's recipe book", clock);
        recipe.SetDirections(
            "Bring a large pot of salted water to the boil.\n\n" +
            "Cook pasta according to package directions until al dente.\n\n" +
            "# For the sauce\n\n" +
            "Heat olive oil in a pan over medium heat.\n\n" +
            "Add garlic and cook until fragrant. Add tomatoes and simmer for 15 minutes.\n\n" +
            "Drain pasta and toss with sauce. Season to taste.",
            clock);

        recipe.SetTags([VegetarianTagId, SpicyTagId], clock);

        recipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId,  400m, GramUnitId, GroupHeading: "Pasta",  Ordinal: 1),
            new IngredientLine(TomatoId, 500m, GramUnitId, GroupHeading: "Sauce",  Ordinal: 2),
            new IngredientLine(GarlicId, 3m,   EachUnitId, GroupHeading: "Sauce",  Ordinal: 3),
            // Untracked staple: no qty/unit (C12 — "to taste")
            new IngredientLine(SaltId, Quantity: null, UnitId: null, GroupHeading: "Sauce", Ordinal: 4),
        ], clock);

        // Minimal JPEG stub so HasPhoto=true and the hero img link renders.
        recipe.SetPhoto([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg", sha256: null, clock);

        return recipe;
    }

    /// <summary>Products the page resolves ingredient names from.</summary>
    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [PastaId]  = new(PastaId,  "Rigatoni",       TrackStock: true,  GramUnitId, null, false, []),
            [TomatoId] = new(TomatoId, "Canned Tomatoes", TrackStock: true, GramUnitId, null, false, []),
            [GarlicId] = new(GarlicId, "Garlic Cloves",   TrackStock: true, EachUnitId, null, false, []),
            [SaltId]   = new(SaltId,   "Salt",            TrackStock: false, EachUnitId, null, false, []),
        };

    /// <summary>Unit codes the page resolves ingredient quantities against.</summary>
    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [GramUnitId] = "g",
            [EachUnitId] = "ea",
        };

    /// <summary>Tag names the page resolves tag pills from.</summary>
    public static IReadOnlyDictionary<TagId, string> TagNames() =>
        new Dictionary<TagId, string>
        {
            [VegetarianTagId] = "Vegetarian",
            [SpicyTagId]      = "Spicy",
        };
}

/// <summary>
/// In-memory <see cref="ITagRepository"/> for the recipe detail L4 tests.
/// <see cref="ResolveNamesAsync"/> returns the dictionary the fixture defines, filtered to
/// the requested ids — mirroring the real household-scoped query.
/// </summary>
public sealed class FakeTagRepository(IReadOnlyDictionary<TagId, string> tagNames) : ITagRepository
{
    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult<Tag?>(null);

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        IReadOnlyDictionary<TagId, string> result = ids
            .Where(tagNames.ContainsKey)
            .ToDictionary(id => id, id => tagNames[id]);
        return Task.FromResult(result);
    }

    public Task AddAsync(Tag tag, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Tag>>([]);
}

/// <summary>
/// In-memory <see cref="IRecipeRepository"/> for the recipe detail L4 tests.
/// Only returns the fixture recipe when the household and id match, mirroring the
/// real household-scoped query-filter + RLS behaviour.
/// </summary>
public sealed class FakeRecipeRepository(ITenantContext tenant, Recipe recipe) : IRecipeRepository
{
    public Task AddAsync(Recipe r, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<Recipe?>(null);
        if (recipe.HouseholdId.Value != hid)   return Task.FromResult<Recipe?>(null);
        if (recipe.Id != id)                   return Task.FromResult<Recipe?>(null);
        return Task.FromResult<Recipe?>(recipe);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([]);
}

/// <summary>
/// In-memory <see cref="ICatalogProductReader"/> for the recipe detail L4 tests. Resolves the batch
/// summary/unit-code paths the Detail page uses from the fixture's product and unit-code maps,
/// filtered to the requested ids — mirroring the real household-scoped queries.
/// </summary>
public sealed class FakeCatalogProductReader(
    IReadOnlyDictionary<Guid, CatalogProduct> products,
    IReadOnlyDictionary<Guid, string> unitCodes)
    : ICatalogProductReader
{
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(
        string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(products.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(id, products[id].Name, products[id].TrackStock));
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = unitIds
            .Where(unitCodes.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => unitCodes[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);
}
