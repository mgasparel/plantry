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
}

internal sealed class FakeTagRepository : ITagRepository
{
    public List<Tag> Items { get; } = [];

    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(t =>
            t.HouseholdId == householdId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(
        IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
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
}

/// <summary>
/// Records the two inline Catalog mutations. A created staple is registered into the paired
/// <see cref="FakeCatalogProductReader"/> (so a follow-up resolve sees it), and a written conversion is
/// registered into the paired <see cref="FakeUnitConverter"/> (so the retry's path check passes).
/// </summary>
internal sealed class FakeCatalogWriter(FakeCatalogProductReader reader, FakeUnitConverter converter) : ICatalogWriter
{
    public List<(string Name, Guid DefaultUnitId)> StaplesCreated { get; } = [];
    public List<(Guid ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor)> ConversionsAdded { get; } = [];

    public Task<Guid> CreateUntrackedStapleAsync(string name, Guid defaultUnitId, CancellationToken ct = default)
    {
        StaplesCreated.Add((name, defaultUnitId));
        var staple = new CatalogProduct(Guid.CreateVersion7(), name, TrackStock: false, defaultUnitId, null, false, []);
        reader.Register(staple);
        return Task.FromResult(staple.Id);
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
