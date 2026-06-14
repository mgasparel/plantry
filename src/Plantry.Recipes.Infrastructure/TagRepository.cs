using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Infrastructure;

public sealed class TagRepository(RecipesDbContext db) : ITagRepository
{
    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        db.Tags.FirstOrDefaultAsync(t => t.HouseholdId == householdId && t.Name.ToLower() == name.ToLower(), ct);

    public async Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<TagId, string>();
        var idList = ids.ToList();
        return await db.Tags
            .Where(t => idList.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
    }

    public async Task AddAsync(Tag tag, CancellationToken ct = default) =>
        await db.Tags.AddAsync(tag, ct);

    public async Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct = default) =>
        await db.Tags.OrderBy(t => t.Name).ToListAsync(ct);
}
