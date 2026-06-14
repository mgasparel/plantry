using Microsoft.EntityFrameworkCore;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;

namespace Plantry.Recipes.Infrastructure;

public sealed class TagRepository(RecipesDbContext db) : ITagRepository
{
    public Task<Tag?> FindByNameAsync(HouseholdId householdId, string name, CancellationToken ct = default) =>
        db.Tags.FirstOrDefaultAsync(t => t.HouseholdId == householdId && t.Name.ToLower() == name.ToLower(), ct);

    public async Task AddAsync(Tag tag, CancellationToken ct = default) =>
        await db.Tags.AddAsync(tag, ct);
}
