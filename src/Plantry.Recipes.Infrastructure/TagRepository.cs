using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Infrastructure;

public sealed class TagRepository(RecipesDbContext db) : ITagRepository
{
    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        db.Tags.FirstOrDefaultAsync(t => t.HouseholdId == householdId && t.Name.ToLower() == name.ToLower(), ct);

    public Task<Tag?> GetByIdAsync(TagId id, CancellationToken ct = default) =>
        db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlySet<TagId>> ResolveExistingIdsAsync(IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new HashSet<TagId>();
        var idList = ids.ToList();
        // Archived tags are intentionally included (matching GetByIdAsync) so an applied-then-archived tag
        // still resolves. RLS/query filter scopes to the household; PK projection only — no Tag loaded.
        var found = await db.Tags.Where(t => idList.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
        return found.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<TagId, string>> ResolveNamesAsync(IReadOnlyList<TagId> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<TagId, string>();
        var idList = ids.ToList();
        // Archived tags are intentionally included so existing recipe references never go blank.
        return await db.Tags
            .Where(t => idList.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
    }

    public async Task AddAsync(Tag tag, CancellationToken ct = default) =>
        await db.Tags.AddAsync(tag, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<Tag>> ListAllAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var query = db.Tags.AsQueryable();
        if (activeOnly)
            query = query.Where(t => t.ArchivedAt == null);
        return await query.OrderBy(t => t.Name).ToListAsync(ct);
    }
}
