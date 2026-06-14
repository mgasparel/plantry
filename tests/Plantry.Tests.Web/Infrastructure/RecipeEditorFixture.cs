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
using RecipesDomain = Plantry.Recipes.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Builds deterministic <see cref="Recipe"/> instances for L4 snapshot tests of the recipe editor page.
/// Two scenarios are offered:
/// <list type="bullet">
///   <item><see cref="BuildEmpty"/> — a recipe with no ingredients (create-mode approximation; used by
///   editing an otherwise-empty recipe to verify the empty-row initial state).</item>
///   <item><see cref="BuildRich"/> — a recipe with several ingredient rows across two groups, including
///   an untracked staple, pre-populated tags, and directions; exercises the full pre-population path the
///   edit GET renders into the Alpine x-data initialiser.</item>
/// </list>
/// </summary>
public static class RecipeEditorFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // Recipe ids — fixed so GUID-scrubber in snapshots maps them to stable tokens.
    public static readonly RecipeId EmptyRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("cccccccc-0000-0000-0000-000000000003"));
    public static readonly RecipeId RichRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("dddddddd-0000-0000-0000-000000000004"));

    // Product ids.
    public static readonly Guid TomatoId  = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GarlicId  = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PastaId   = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid SaltId    = Guid.Parse("44444444-4444-4444-4444-444444444444"); // untracked staple
    public static readonly Guid ChiliId   = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // Unit ids.
    public static readonly Guid GramUnitId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid EachUnitId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    // Tag ids.
    public static readonly TagId VegetarianTagId = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    public static readonly TagId QuickTagId      = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));

    /// <summary>
    /// A recipe with no ingredients — approximates the state of a freshly opened edit form for a recipe
    /// that has not yet had ingredients added. The edit GET will render a single blank ingredient row
    /// seeded via <c>Input.Lines = [new IngredientRowInput { Ordinal = 0 }]</c>.
    /// </summary>
    public static Recipe BuildEmpty()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var recipe = Recipe.Create(hid, "Empty Recipe", defaultServings: 2, Plantry.SharedKernel.Domain.SystemClock.Instance).Value;
        // Use a fixed id via reflection to keep snapshots stable.
        SetId(recipe, EmptyRecipeId);
        return recipe;
    }

    /// <summary>
    /// A recipe with a full set of ingredients (two groups, one untracked staple), tags, cook time,
    /// source and directions — exercises every pre-population branch of the edit GET handler.
    /// </summary>
    public static Recipe BuildRich()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var recipe = Recipe.Create(hid, "Spicy Arrabbiata", defaultServings: 4, clock).Value;
        SetId(recipe, RichRecipeId);

        recipe.SetCookTime(25, clock);
        recipe.SetSource("Grandma's notes", clock);
        recipe.SetDirections(
            "Boil a large pot of salted water.\n\n" +
            "# Sauce\n\n" +
            "Fry garlic and chili in olive oil.\n\n" +
            "Add tomatoes and simmer 15 minutes.\n\n" +
            "Drain pasta, toss with sauce.",
            clock);
        recipe.SetTags([VegetarianTagId, QuickTagId], clock);
        recipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId,   400m, GramUnitId, GroupHeading: "Pasta",  Ordinal: 0),
            new IngredientLine(TomatoId,  300m, GramUnitId, GroupHeading: "Sauce",  Ordinal: 1),
            new IngredientLine(GarlicId,  3m,   EachUnitId, GroupHeading: "Sauce",  Ordinal: 2),
            new IngredientLine(ChiliId,   2m,   EachUnitId, GroupHeading: "Sauce",  Ordinal: 3),
            // Untracked staple — no qty/unit (C12 "to taste")
            new IngredientLine(SaltId, Quantity: null, UnitId: null, GroupHeading: "Sauce", Ordinal: 4),
        ], clock);

        return recipe;
    }

    /// <summary>Products the editor resolves ingredient names + unit codes from (used in edit GET).</summary>
    public static IReadOnlyDictionary<Guid, CatalogProductSummary> ProductSummaries() =>
        new Dictionary<Guid, CatalogProductSummary>
        {
            [PastaId]  = new(PastaId,  "Rigatoni",       TrackStock: true),
            [TomatoId] = new(TomatoId, "Canned Tomatoes", TrackStock: true),
            [GarlicId] = new(GarlicId, "Garlic Cloves",   TrackStock: true),
            [ChiliId]  = new(ChiliId,  "Dried Chili",     TrackStock: true),
            [SaltId]   = new(SaltId,   "Salt",            TrackStock: false),
        };

    /// <summary>Unit codes the page resolves ingredient quantities against.</summary>
    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [GramUnitId] = "g",
            [EachUnitId] = "ea",
        };

    /// <summary>Unit options for the unit dropdown in the ingredient rows.</summary>
    public static IReadOnlyList<CatalogUnitOption> UnitOptions() =>
    [
        new(GramUnitId, "g"),
        new(EachUnitId, "ea"),
    ];

    /// <summary>Tag names for the tag pre-population in edit GET.</summary>
    public static IReadOnlyDictionary<TagId, string> TagNames() =>
        new Dictionary<TagId, string>
        {
            [VegetarianTagId] = "Vegetarian",
            [QuickTagId]      = "Quick",
        };

    // ── internal helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the recipe's <see cref="RecipesDomain.Recipe.Id"/> to a deterministic value via reflection
    /// so snapshot GUID-scrubbing maps it to a stable token rather than a random per-run guid.
    /// <para>
    /// <c>Entity&lt;TId&gt;.Id</c> has a <c>protected set</c>; we invoke the setter via reflection
    /// rather than adding a test seam to the domain.
    /// </para>
    /// </summary>
    private static void SetId(Recipe recipe, RecipeId id)
    {
        var prop = typeof(Recipe).BaseType?.BaseType?  // Entity<RecipeId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(recipe, id);
    }
}

// ── Fake adapters the editor GET depends on ──────────────────────────────────────

/// <summary>
/// In-memory <see cref="IRecipeRepository"/> for the recipe editor L4 tests. Returns the registered
/// fixture recipe when household and id match; ignores write operations.
/// </summary>
public sealed class FakeEditorRecipeRepository(ITenantContext tenant, params Recipe[] knownRecipes)
    : IRecipeRepository
{
    public Task AddAsync(Recipe r, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<Recipe?>(null);
        var recipe = knownRecipes.FirstOrDefault(r => r.Id == id && r.HouseholdId.Value == hid);
        return Task.FromResult(recipe);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>
/// In-memory <see cref="ICatalogProductReader"/> for the editor L4 tests — returns the fixture
/// product set for the read paths the GET handler calls (batch summaries + unit codes + unit list).
/// </summary>
public sealed class FakeEditorProductReader(
    IReadOnlyDictionary<Guid, CatalogProductSummary> summaries,
    IReadOnlyDictionary<Guid, string> unitCodes,
    IReadOnlyList<CatalogUnitOption> unitOptions)
    : ICatalogProductReader
{
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<CatalogProduct?>(null);

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(
        string nameQuery, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductCandidate>>([]);

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(summaries.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => summaries[id]);
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
        Task.FromResult(unitOptions);
}
