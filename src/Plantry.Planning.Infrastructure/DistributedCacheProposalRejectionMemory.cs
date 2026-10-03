using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;

namespace Plantry.Planning.Infrastructure;

/// <summary>
/// <see cref="IProposalRejectionMemory"/> backed by <see cref="IDistributedCache"/>. Stored under
/// <c>{storeKey}:rejected</c> with the same 2-hour sliding expiry as the pending proposals.
/// </summary>
public class DistributedCacheProposalRejectionMemory(IDistributedCache cache, ILogger<DistributedCacheProposalRejectionMemory> logger) : IProposalRejectionMemory
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(2),
    };

    private static string KeyFor(string storeKey) => $"{storeKey}:rejected";

    public async Task<IReadOnlyDictionary<string, IReadOnlySet<Guid>>> GetAsync(string storeKey, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(KeyFor(storeKey), ct);
        if (raw is null) return new Dictionary<string, IReadOnlySet<Guid>>();

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<Guid>>>(raw) ?? [];
            return stored.ToDictionary(p => p.Key, p => (IReadOnlySet<Guid>)p.Value.ToHashSet());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding unreadable proposal rejection memory for store key {StoreKey}.", storeKey);
            return new Dictionary<string, IReadOnlySet<Guid>>();
        }
    }

    public async Task RememberAsync(string storeKey, IEnumerable<ProposedMeal> rejected, CancellationToken ct = default)
    {
        var additions = rejected
            .GroupBy(p => IProposalRejectionMemory.CellKey(p.Date, p.MealSlotId))
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Dishes).Select(d => d.RecipeId).ToList());
        if (additions.Count == 0) return;

        var current = await GetAsync(storeKey, ct);
        var merged = current.ToDictionary(p => p.Key, p => p.Value.ToList());
        foreach (var (cell, recipeIds) in additions)
        {
            if (!merged.TryGetValue(cell, out var existing)) merged[cell] = existing = [];
            foreach (var id in recipeIds)
                if (!existing.Contains(id)) existing.Add(id);
        }

        await cache.SetStringAsync(KeyFor(storeKey), JsonSerializer.Serialize(merged), CacheOptions, ct);
    }
}
