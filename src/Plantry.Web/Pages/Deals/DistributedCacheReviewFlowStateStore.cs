using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// <see cref="IReviewFlowStateStore"/> backed by <see cref="IDistributedCache"/> (the app's in-process
/// distributed-memory cache in Phase 3). The demoted + unchecked deal-id sets are serialised as one JSON blob
/// under the caller's <c>{household}_{session}</c> key with a 2-hour sliding expiry — the same lifetime and
/// mechanism as <c>DistributedCachePendingProposalStore</c>. Writes read-modify-write the blob; concurrent
/// step-1 toggles within one session are effectively serial (the user drives one checkbox at a time).
/// </summary>
public sealed class DistributedCacheReviewFlowStateStore(IDistributedCache cache) : IReviewFlowStateStore
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromHours(2),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ReviewFlowState> GetAsync(string storeKey, CancellationToken ct = default)
    {
        var raw = await cache.GetStringAsync(storeKey, ct);
        return Deserialize(raw);
    }

    public async Task SetUncheckedAsync(string storeKey, Guid dealId, bool isUnchecked, CancellationToken ct = default)
    {
        var dto = await LoadDtoAsync(storeKey, ct);
        var unchecked_ = dto.Unchecked.ToHashSet();

        var changed = isUnchecked ? unchecked_.Add(dealId) : unchecked_.Remove(dealId);
        if (!changed)
            return;

        await SaveAsync(storeKey, new FlowStateDto(dto.Demoted, [.. unchecked_]), ct);
    }

    public async Task CommitAsync(
        string storeKey, IEnumerable<Guid> demote, IEnumerable<Guid> clearUnchecked, CancellationToken ct = default)
    {
        var dto = await LoadDtoAsync(storeKey, ct);

        var demoted = dto.Demoted.ToHashSet();
        demoted.UnionWith(demote);

        var unchecked_ = dto.Unchecked.ToHashSet();
        unchecked_.ExceptWith(clearUnchecked);

        await SaveAsync(storeKey, new FlowStateDto([.. demoted], [.. unchecked_]), ct);
    }

    private async Task<FlowStateDto> LoadDtoAsync(string storeKey, CancellationToken ct)
    {
        var raw = await cache.GetStringAsync(storeKey, ct);
        if (raw is null)
            return new FlowStateDto([], []);
        try
        {
            return JsonSerializer.Deserialize<FlowStateDto>(raw, JsonOptions) ?? new FlowStateDto([], []);
        }
        catch (JsonException)
        {
            return new FlowStateDto([], []);
        }
    }

    private async Task SaveAsync(string storeKey, FlowStateDto dto, CancellationToken ct)
    {
        if (dto.Demoted.Count == 0 && dto.Unchecked.Count == 0)
        {
            await cache.RemoveAsync(storeKey, ct);
            return;
        }
        await cache.SetStringAsync(storeKey, JsonSerializer.Serialize(dto, JsonOptions), CacheOptions, ct);
    }

    private static ReviewFlowState Deserialize(string? raw)
    {
        if (raw is null)
            return ReviewFlowState.Empty;
        try
        {
            var dto = JsonSerializer.Deserialize<FlowStateDto>(raw, JsonOptions);
            if (dto is null)
                return ReviewFlowState.Empty;
            return new ReviewFlowState(dto.Demoted.ToHashSet(), dto.Unchecked.ToHashSet());
        }
        catch (JsonException)
        {
            return ReviewFlowState.Empty;
        }
    }

    private sealed record FlowStateDto(List<Guid> Demoted, List<Guid> Unchecked);
}
