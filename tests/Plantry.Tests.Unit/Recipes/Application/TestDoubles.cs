using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Tests.Unit.Recipes.Application;

internal sealed class FakeTenantContext(Guid? householdId) : ITenantContext
{
    public Guid? HouseholdId { get; } = householdId;
}

internal sealed class FakeRecipeRepository : IRecipeRepository
{
    public List<Recipe> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task AddAsync(Recipe recipe, CancellationToken ct = default)
    {
        Items.Add(recipe);
        return Task.CompletedTask;
    }

    public Task<Recipe?> GetByIdAsync(RecipeId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(r => r.Id == id));

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<bool> NameExistsAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(r =>
            r.HouseholdId == householdId && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Recipe>> ListForBrowseAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Recipe>>(Items.Where(r => r.ArchivedAt == null).OrderBy(r => r.Name).ToList());

    public Task<IReadOnlySet<RecipeId>> ListRecipeIdsWithPhotoAsync(CancellationToken ct = default)
    {
        IReadOnlySet<RecipeId> result = Items.Where(r => r.Photo is not null).Select(r => r.Id).ToHashSet();
        return Task.FromResult(result);
    }

    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(r => r.HouseholdId == householdId && r.ArchivedAt == null));

    public Task<IReadOnlyDictionary<RecipeId, string>> GetRecipeNamesByIdAsync(
        IReadOnlyList<RecipeId> ids, CancellationToken ct = default)
    {
        var wanted = ids.ToHashSet();
        IReadOnlyDictionary<RecipeId, string> result = Items
            .Where(r => wanted.Contains(r.Id) && r.ArchivedAt == null)
            .ToDictionary(r => r.Id, r => r.Name);
        return Task.FromResult(result);
    }
}

internal sealed class FakeTagRepository : ITagRepository
{
    public List<Tag> Items { get; } = [];
    public int SaveChangesCalls { get; private set; }

    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(t =>
            t.HouseholdId == householdId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<Tag?> GetByIdAsync(TagId id, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(t => t.Id == id));

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        // Archived tags are included intentionally so existing recipe references render.
        IReadOnlyDictionary<TagId, string> result = Items
            .Where(t => ids.Contains(t.Id))
            .ToDictionary(t => t.Id, t => t.Name);
        return Task.FromResult(result);
    }

    public Task AddAsync(Tag tag, CancellationToken ct = default)
    {
        Items.Add(tag);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveChangesCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Tag>> ListAllAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var query = Items.AsEnumerable();
        if (activeOnly) query = query.Where(t => !t.IsArchived);
        return Task.FromResult<IReadOnlyList<Tag>>(query.OrderBy(t => t.Name).ToList());
    }
}

internal sealed class FakeCatalogProductReader : ICatalogProductReader
{
    private readonly Dictionary<Guid, CatalogProduct> _products = [];

    public CatalogProduct AddTracked(Guid defaultUnitId, string name = "Flour")
    {
        var p = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: true, defaultUnitId, null, false, []);
        _products[p.Id] = p;
        return p;
    }

    public void Register(CatalogProduct product) => _products[product.Id] = product;

    public Task<CatalogProduct?> FindAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<IReadOnlyList<CatalogProductCandidate>> SearchAsync(string nameQuery, CancellationToken ct = default)
    {
        IReadOnlyList<CatalogProductCandidate> hits = _products.Values
            .Where(p => p.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            .Select(p => new CatalogProductCandidate(p.Id, p.Name, p.TrackStock, p.DefaultUnitId))
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<IReadOnlyDictionary<Guid, CatalogProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, CatalogProductSummary> result = productIds
            .Where(_products.ContainsKey)
            .Distinct()
            .ToDictionary(id => id, id => new CatalogProductSummary(id, _products[id].Name, _products[id].TrackStock));
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task<IReadOnlyList<CatalogUnitOption>> ListUnitsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogUnitOption>>([]);

    public Task<IReadOnlyList<CatalogGroupOption>> ListGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogGroupOption>>([]);

    public Task<IReadOnlyList<CatalogCategoryOption>> ListCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogCategoryOption>>([]);
}

/// <summary>
/// Records inline Catalog mutations for AuthorRecipe unit tests. Created products are registered into
/// the paired <see cref="FakeCatalogProductReader"/> (so a follow-up resolve sees them), and written
/// conversions are registered into the paired <see cref="FakeUnitConverter"/> (so the retry's path
/// check passes).
/// </summary>
internal sealed class FakeCatalogWriter(FakeCatalogProductReader reader, FakeUnitConverter converter) : ICatalogWriter
{
    public List<(string Name, Guid DefaultUnitId)> StaplesCreated { get; } = [];
    public List<(string Name, Guid DefaultUnitId, Guid? CategoryId)> TrackedProductsCreated { get; } = [];
    public List<(Guid ParentGroupId, string VariantName)> VariantsCreated { get; } = [];
    public List<(string GroupName, string VariantName, Guid DefaultUnitId)> GroupedProductsCreated { get; } = [];
    public List<(Guid ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor)> ConversionsAdded { get; } = [];

    public Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default)
    {
        StaplesCreated.Add((name, defaultUnitId));
        var staple = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: false, defaultUnitId, null, false, []);
        reader.Register(staple);
        return Task.FromResult(staple.Id);
    }

    public Task<Guid> CreateTrackedProductAsync(string name, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default)
    {
        TrackedProductsCreated.Add((name, defaultUnitId, categoryId));
        var product = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: true, defaultUnitId, null, false, []);
        reader.Register(product);
        return Task.FromResult(product.Id);
    }

    public Task<Guid> CreateTrackedVariantAsync(Guid parentGroupId, string variantName, Guid? unitOverride, Guid? categoryOverride, CancellationToken ct = default)
    {
        VariantsCreated.Add((parentGroupId, variantName));
        // The variant has the parent's unit (simplified in the fake — unit override not tracked for simplicity).
        var defaultUnitId = unitOverride ?? Guid.CreateVersion7();
        var variant = new CatalogProduct(Guid.CreateVersion7(), variantName, TrackStock: true, defaultUnitId, parentGroupId, false, []);
        reader.Register(variant);
        return Task.FromResult(variant.Id);
    }

    public Task<Guid> CreateTrackedGroupedProductAsync(string groupName, string variantName, Guid defaultUnitId, Guid? categoryId, CancellationToken ct = default)
    {
        GroupedProductsCreated.Add((groupName, variantName, defaultUnitId));
        var variantId = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();
        // Register only the variant — the group is an abstract parent and is not directly resolved.
        var variant = new CatalogProduct(variantId, variantName, TrackStock: true, defaultUnitId, groupId, false, []);
        reader.Register(variant);
        return Task.FromResult(variantId);
    }

    public Task AddConversionAsync(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor, CancellationToken ct = default)
    {
        ConversionsAdded.Add((productId, fromUnitId, toUnitId, factor));
        converter.AddPath(productId, fromUnitId, toUnitId, factor);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitConverter : IUnitConverter
{
    private readonly Dictionary<(Guid Product, Guid From, Guid To), decimal> _paths = [];

    public void AddPath(Guid productId, Guid fromUnitId, Guid toUnitId, decimal factor) =>
        _paths[(productId, fromUnitId, toUnitId)] = factor;

    public Task<Result<decimal>> ConvertAsync(
        Guid productId, decimal amount, Guid fromUnitId, Guid toUnitId, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId)
            return Task.FromResult(Result<decimal>.Success(amount));
        if (_paths.TryGetValue((productId, fromUnitId, toUnitId), out var factor))
            return Task.FromResult(Result<decimal>.Success(amount * factor));
        return Task.FromResult(Result<decimal>.Failure(
            Error.Custom("Catalog.NoConversionPath", "No conversion path.")));
    }
}
