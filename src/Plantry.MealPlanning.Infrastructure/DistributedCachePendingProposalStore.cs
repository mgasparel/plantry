using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;

namespace Plantry.MealPlanning.Infrastructure;

/// <summary>
/// <see cref="IPendingProposalStore"/> backed by <see cref="IDistributedCache"/>.
/// Proposals are serialised as JSON and stored with a 2-hour sliding expiry.
/// The store key format is <c>{householdId}_{weekStart:yyyyMMdd}_{sessionId}</c> (set by caller).
/// </summary>
public class DistributedCachePendingProposalStore(IDistributedCache cache) : IPendingProposalStore
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(2),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<ProposedMeal>> GetAsync(string storeKey, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(storeKey, ct);
        if (raw is null) return [];

        try
        {
            var dtos = JsonSerializer.Deserialize<List<ProposedMealDto>>(raw, JsonOptions) ?? [];
            return dtos.Select(d => d.ToDomain()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SetAsync(string storeKey, IReadOnlyList<ProposedMeal> proposals, CancellationToken ct = default)
    {
        var dtos = proposals.Select(ProposedMealDto.FromDomain).ToList();
        var json = JsonSerializer.Serialize(dtos, JsonOptions);
        await cache.SetStringAsync(storeKey, json, CacheOptions, ct);
    }

    public async Task RemoveAsync(string storeKey, DateOnly date, MealSlotId slotId, CancellationToken ct = default)
    {
        var current = await GetAsync(storeKey, ct);
        if (current.Count == 0) return;

        var updated = current
            .Where(p => !(p.Date == date && p.MealSlotId == slotId))
            .ToList();

        if (updated.Count == 0)
            await cache.RemoveAsync(storeKey, ct);
        else
            await SetAsync(storeKey, updated, ct);
    }

    public async Task ClearAsync(string storeKey, CancellationToken ct = default)
    {
        await cache.RemoveAsync(storeKey, ct);
    }

    // ── DTO types for serialisation ───────────────────────────────────────────────

    private sealed record ProposedMealDto(
        string Date,
        string MealSlotId,
        List<Guid> EffectiveAttendees,
        List<ProposedDishDto> Dishes,
        string? Reasoning)
    {
        public static ProposedMealDto FromDomain(ProposedMeal m) => new(
            m.Date.ToString("yyyy-MM-dd"),
            m.MealSlotId.Value.ToString("N"),
            [..m.EffectiveAttendees],
            m.Dishes.Select(d => new ProposedDishDto(d.RecipeId, d.Servings, d.Ordinal)).ToList(),
            m.Reasoning);

        public ProposedMeal ToDomain() => new(
            DateOnly.Parse(Date),
            Domain.MealSlotId.From(Guid.Parse(MealSlotId)),
            EffectiveAttendees,
            Dishes.Select(d => new ProposedDish(d.RecipeId, d.Servings, d.Ordinal)).ToList(),
            Reasoning);
    }

    private sealed record ProposedDishDto(Guid RecipeId, int Servings, int Ordinal);
}
