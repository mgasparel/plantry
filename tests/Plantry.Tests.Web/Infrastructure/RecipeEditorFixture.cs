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
    // Non-canonical recipe — stored ingredient order deliberately defeats the CanonicaliseSectionOrder
    // no-op so the edit-GET reorder path (plantry-21if) is exercised end-to-end.
    public static readonly RecipeId NonCanonicalRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("ffffffff-0000-0000-0000-000000000006"));
    // Recipe with a stored photo — exercises the editor's current-photo preview (plantry-nj0e).
    public static readonly RecipeId PhotoRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("0a000000-0000-0000-0000-000000000008"));

    // Product ids.
    public static readonly Guid TomatoId  = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GarlicId  = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid PastaId   = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid SaltId    = Guid.Parse("44444444-4444-4444-4444-444444444444"); // untracked staple
    public static readonly Guid ChiliId   = Guid.Parse("55555555-5555-5555-5555-555555555555");
    // plantry-obg3: a product whose DefaultUnitId dangles outside the household unit list (see
    // MissingDefaultUnitId) — the "olive oil with an unresolvable stock unit" dogfood condition. The
    // CheckConversion handler must return the explicit missing-default flag for it, never a half-empty
    // needsConversion:true payload.
    public static readonly Guid DanglingDefaultId = Guid.Parse("aabbccdd-0000-0000-0000-0000000000aa");

    // Unit ids.
    public static readonly Guid GramUnitId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid EachUnitId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    // plantry-obg3: a unit id deliberately ABSENT from UnitOptions()/UnitCodes(), so a product pointing
    // at it as its default unit resolves to no unit — the unresolvable-default scenario.
    public static readonly Guid MissingDefaultUnitId = Guid.Parse("dead0000-0000-0000-0000-0000000000de");

    // Tag ids.
    public static readonly TagId VegetarianTagId = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    public static readonly TagId QuickTagId      = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    // Archived tag — applied to the rich recipe but absent from the active picker dropdown.
    public static readonly TagId ArchivedTagId   = new(Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"));

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
    /// A recipe that carries a stored photo (plantry-nj0e). Mirrors <see cref="BuildEmpty"/> but calls
    /// <see cref="Recipe.SetPhoto"/> so <c>recipe.Photo is not null</c> — driving the editor's
    /// current-photo preview branch (<c>!IsCreate &amp;&amp; HasPhoto</c>). SetPhoto runs after
    /// <see cref="SetId"/> because <see cref="RecipePhoto"/> is keyed on the recipe id.
    /// </summary>
    public static Recipe BuildWithPhoto()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var recipe = Recipe.Create(hid, "Photo Recipe", defaultServings: 2, clock).Value;
        SetId(recipe, PhotoRecipeId);
        recipe.SetPhoto(new byte[] { 1, 2, 3, 4 }, "image/png", sha256: null, clock);
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

    /// <summary>
    /// A recipe whose STORED ingredient order is deliberately non-canonical, so the edit-GET
    /// <c>CanonicaliseSectionOrder</c> pass (Edit.cshtml.cs) is forced to actually reorder — unlike
    /// <see cref="BuildRich"/>, which is already canonical and leaves that method a no-op.
    ///
    /// <para>Stored order (by <c>Ordinal</c>) interleaves headings Sauce / Ungrouped / Topping / Sauce
    /// and places ungrouped ingredients at Ordinal &gt; 0:</para>
    /// <list type="number">
    ///   <item>Ordinal 0 — Tomato, heading "Sauce"</item>
    ///   <item>Ordinal 1 — Pasta, ungrouped (ungrouped at Ordinal &gt; 0)</item>
    ///   <item>Ordinal 2 — Chili, heading "Topping"</item>
    ///   <item>Ordinal 3 — Garlic, heading "Sauce"</item>
    ///   <item>Ordinal 4 — Salt, ungrouped (untracked staple, no qty/unit)</item>
    /// </list>
    /// <para>The canonical edit-GET output must therefore be: ungrouped first (Pasta, Salt), then the
    /// "Sauce" section in first-appearance order (Tomato, Garlic), then "Topping" (Chili), with ordinals
    /// renumbered contiguous 0..4. Removing the ungrouped-first hoist or the first-seen heading pass
    /// changes this order, turning the load-bearing test red.</para>
    /// </summary>
    public static Recipe BuildNonCanonical()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var recipe = Recipe.Create(hid, "Non-Canonical Order", defaultServings: 3, clock).Value;
        SetId(recipe, NonCanonicalRecipeId);

        recipe.ReplaceIngredients(
        [
            new IngredientLine(TomatoId, 300m, GramUnitId, GroupHeading: "Sauce",   Ordinal: 0),
            new IngredientLine(PastaId,  400m, GramUnitId, GroupHeading: null,      Ordinal: 1), // ungrouped @ ord>0
            new IngredientLine(ChiliId,  2m,   EachUnitId, GroupHeading: "Topping", Ordinal: 2),
            new IngredientLine(GarlicId, 3m,   EachUnitId, GroupHeading: "Sauce",   Ordinal: 3),
            // Untracked staple — no qty/unit (C12 "to taste"), second ungrouped row.
            new IngredientLine(SaltId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 4),
        ], clock);

        return recipe;
    }

    /// <summary>
    /// plantry-429l: a recipe with a single ingredient referencing a now-<b>tracked</b> product
    /// (<see cref="TomatoId"/>, TrackStock:true in <see cref="ProductSummaries"/>) but stored with a null
    /// quantity and unit — the shape produced when a line authored while its product was untracked has its
    /// product later flipped tracked. The edit GET must flag this row (tracked + missing qty/unit) and
    /// prefill its unit from the product default, rather than dead-ending on an opaque global save error.
    /// </summary>
    public static readonly RecipeId FlipToTrackedRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("cafe0000-0000-0000-0000-000000000007"));

    public static Recipe BuildFlipToTracked()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var recipe = Recipe.Create(hid, "Flipped Staple Recipe", defaultServings: 2, clock).Value;
        SetId(recipe, FlipToTrackedRecipeId);
        recipe.ReplaceIngredients(
        [
            // Authored while untracked (null qty/unit legal); the product is now tracked → R5 would reject.
            new IngredientLine(TomatoId, Quantity: null, UnitId: null, GroupHeading: null, Ordinal: 0),
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
            [DanglingDefaultId] = new(DanglingDefaultId, "Olive Oil", TrackStock: true),
        };

    /// <summary>Unit codes the page resolves ingredient quantities against.</summary>
    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [GramUnitId] = "g",
            [EachUnitId] = "ea",
        };

    /// <summary>
    /// Unit options for the unit dropdown in the ingredient rows. Carries Dimension + FactorToBase
    /// (plantry-qno9) so the CheckConversion handler can build the axis-locked stock/recipe unit lists:
    /// gram is mass, each is count — a cross-dimension pair, so a conversion is always required between them.
    /// </summary>
    public static IReadOnlyList<CatalogUnitOption> UnitOptions() =>
    [
        new(GramUnitId, "g",  "mass",  1m),
        new(EachUnitId, "ea", "count", 1m),
    ];

    /// <summary>
    /// Default unit ids per product for use with <see cref="FakeEditorProductReader"/> on the POST path.
    /// Ensures the conversion check (<c>unit != defaultUnitId</c>) passes without needing
    /// <see cref="FakeUnitConverter"/> when unit and default unit are the same.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> ProductDefaultUnits() =>
        new Dictionary<Guid, Guid>
        {
            [PastaId]  = GramUnitId,
            [TomatoId] = GramUnitId,
            [GarlicId] = EachUnitId,
            [ChiliId]  = EachUnitId,
            [SaltId]   = GramUnitId,
            // plantry-obg3: points at a unit id not present in UnitOptions() — resolves to no unit.
            [DanglingDefaultId] = MissingDefaultUnitId,
        };

    /// <summary>Tag names for the tag pre-population in edit GET (resolve names for pre-selected chips).
    /// Includes archived tags so chips on existing recipes never go blank (mirroring ITagRepository.ResolveNamesAsync).</summary>
    public static IReadOnlyDictionary<TagId, string> TagNames() =>
        new Dictionary<TagId, string>
        {
            [VegetarianTagId] = "Vegetarian",
            [QuickTagId]      = "Quick",
            [ArchivedTagId]   = "Grocer",   // still resolvable even when archived
        };

    /// <summary>
    /// Active tag objects for the recipe editor's closed-vocabulary picker dropdown.
    /// Both fixture tags are active (not archived), ordered alphabetically.
    /// </summary>
    public static IReadOnlyList<Tag> ActiveTags()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var vegetarian = Tag.Create(hid, "Vegetarian", null, clock);
        SetTagId(vegetarian, VegetarianTagId);
        var quick = Tag.Create(hid, "Quick", null, clock);
        SetTagId(quick, QuickTagId);
        return [quick, vegetarian]; // alphabetical: Quick, Vegetarian
    }

    /// <summary>
    /// Full tag list (active + archived) for a <see cref="FakeTagRepository"/> seeded to support the
    /// archived-tag edge-case scenario.
    /// <para>
    /// <see cref="FakeTagRepository.GetByIdAsync"/> looks up tags from this list, so archived tags
    /// applied to existing recipes are still findable. <see cref="FakeTagRepository.ListAllAsync"/>
    /// with <c>activeOnly:true</c> filters by <see cref="Tag.IsArchived"/>, so the picker dropdown
    /// only surfaces active tags even when this full list is seeded.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Tag> AllTagsIncludingArchived()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;
        var vegetarian = Tag.Create(hid, "Vegetarian", null, clock);
        SetTagId(vegetarian, VegetarianTagId);
        var quick = Tag.Create(hid, "Quick", null, clock);
        SetTagId(quick, QuickTagId);
        var grocer = Tag.Create(hid, "Grocer", null, clock);
        SetTagId(grocer, ArchivedTagId);
        grocer.Archive(clock); // archived — excluded from active picker dropdown
        return [quick, vegetarian, grocer];
    }

    /// <summary>
    /// Builds the rich recipe with an additional archived tag applied, for the archived-tag edge-case fixture.
    /// The recipe is separate from <see cref="BuildRich"/> so the existing rich-recipe snapshots remain stable.
    /// </summary>
    public static readonly RecipeId RichArchivedTagRecipeId = RecipesDomain.RecipeId.From(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000005"));

    public static Recipe BuildRichWithArchivedTag()
    {
        var hid = HouseholdId.From(HouseholdAId);
        var clock = Plantry.SharedKernel.Domain.SystemClock.Instance;

        var recipe = Recipe.Create(hid, "Spicy Arrabbiata (Archived Tag)", defaultServings: 4, clock).Value;
        SetId(recipe, RichArchivedTagRecipeId);

        // The archived tag is pre-applied — it shows as a chip on the GET (ResolveNamesAsync still finds it)
        // but is absent from the picker dropdown (ListAllAsync activeOnly:true excludes it).
        recipe.SetTags([VegetarianTagId, ArchivedTagId], clock);
        recipe.ReplaceIngredients(
        [
            new IngredientLine(PastaId, 400m, GramUnitId, GroupHeading: null, Ordinal: 0),
        ], clock);

        return recipe;
    }

    /// <summary>Sets a <see cref="Tag"/>'s id via reflection for deterministic fixture ids.</summary>
    private static void SetTagId(Tag tag, TagId id)
    {
        var prop = typeof(Tag).BaseType?.BaseType?  // Entity<TagId>
            .GetProperty("Id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop?.SetValue(tag, id);
    }

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
    /// <summary>
    /// Captures the last recipe added via <see cref="AddAsync"/> so POST round-trip tests
    /// can assert what was persisted. Null until the first Add call.
    /// </summary>
    public Recipe? LastAdded { get; private set; }

    public Task AddAsync(Recipe r, CancellationToken ct = default)
    {
        LastAdded = r;
        return Task.CompletedTask;
    }

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } hid) return Task.FromResult<Recipe?>(null);
        var recipe = knownRecipes.FirstOrDefault(r => r.Id == id && r.HouseholdId.Value == hid);
        return Task.FromResult(recipe);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>([]);

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<RecipeId>>(new HashSet<RecipeId>());

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RecipeId, string>>(new Dictionary<RecipeId, string>());

    public Task<IReadOnlyList<RecipeInclusionEdge>> ListInclusionEdgesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipeInclusionEdge>>(Edges().ToList());

    /// <summary>
    /// Computes the reverse-includer set from the known recipes' inclusion edges (recipe-composition.md N5/D10) so
    /// reverse-ripple tests can drive real transitive lookups. Recipes with no inclusions yield no edges, so this is
    /// an empty set for the pre-inclusion fixtures — behaviour unchanged for existing callers.
    /// </summary>
    public Task<IReadOnlySet<RecipeId>> GetIncluderIdsAsync(
        RecipeId subRecipeId, bool transitive = false, CancellationToken ct = default)
    {
        var edges = Edges().ToList();
        if (!transitive)
        {
            IReadOnlySet<RecipeId> direct = edges
                .Where(e => e.SubId == subRecipeId)
                .Select(e => e.ParentId)
                .ToHashSet();
            return Task.FromResult(direct);
        }

        var bySub = edges.GroupBy(e => e.SubId).ToDictionary(g => g.Key, g => g.Select(e => e.ParentId).ToList());
        var result = new HashSet<RecipeId>();
        var queue = new Queue<RecipeId>();
        queue.Enqueue(subRecipeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!bySub.TryGetValue(current, out var parents)) continue;
            foreach (var parent in parents)
                if (result.Add(parent))
                    queue.Enqueue(parent);
        }
        IReadOnlySet<RecipeId> transitiveResult = result;
        return Task.FromResult(transitiveResult);
    }

    private IEnumerable<RecipeInclusionEdge> Edges() =>
        knownRecipes.SelectMany(r => r.Inclusions.Select(i => new RecipeInclusionEdge(r.Id, i.SubRecipeId)));
}

/// <summary>
/// In-memory <see cref="ICatalogProductReader"/> for the editor L4 tests — returns the fixture
/// product set for the read paths the GET handler calls (batch summaries + unit codes + unit list).
/// </summary>
public sealed class FakeEditorProductReader(
    IReadOnlyDictionary<Guid, CatalogProductSummary> summaries,
    IReadOnlyDictionary<Guid, string> unitCodes,
    IReadOnlyList<CatalogUnitOption> unitOptions,
    IReadOnlyDictionary<Guid, Guid>? productDefaultUnits = null)
    : ICatalogProductReader
{
    /// <summary>
    /// Returns a <see cref="CatalogProduct"/> from the fixture summaries so that <see cref="AuthorRecipe"/>
    /// can resolve an ingredient line's product on the POST path. The default unit comes from
    /// <paramref name="productDefaultUnits"/> (if supplied) or falls back to the first unit option.
    /// </summary>
    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default)
    {
        if (!summaries.TryGetValue(productId, out var summary)) return Task.FromResult<CatalogProduct?>(null);
        var defaultUnitId = productDefaultUnits?.GetValueOrDefault(productId)
            ?? unitOptions.FirstOrDefault()?.Id
            ?? Guid.Empty;
        var product = new CatalogProduct(
            productId, summary.Name, summary.TrackStock,
            DefaultUnitId: defaultUnitId,
            ParentProductId: null, IsParent: false,
            VariantProductIds: []);
        return Task.FromResult<CatalogProduct?>(product);
    }

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

    /// <summary>
    /// Returns an empty list of group options — the L4 editor tests do not exercise the create-view
    /// Group combobox, so an empty set is correct (the combobox renders with no pre-loaded groups).
    /// </summary>
    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

    /// <summary>
    /// Returns an empty list of category options — the L4 editor tests do not exercise the create-view
    /// Defaults collapsible, so an empty set is correct.
    /// </summary>
    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
}
