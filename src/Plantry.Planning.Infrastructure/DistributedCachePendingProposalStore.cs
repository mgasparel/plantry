using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;

namespace Plantry.Planning.Infrastructure;

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
            m.Dishes.Select(d => new ProposedDishDto(
                d.RecipeId, d.Servings, d.Ordinal, RecipeScoreBreakdownDto.FromDomain(d.ScoreBreakdown))).ToList(),
            m.Reasoning);

        public ProposedMeal ToDomain() => new(
            DateOnly.Parse(Date),
            Domain.MealSlotId.From(Guid.Parse(MealSlotId)),
            EffectiveAttendees,
            Dishes.Select(d => new ProposedDish(d.RecipeId, d.Servings, d.Ordinal, d.ScoreBreakdown?.ToDomain())).ToList(),
            Reasoning);
    }

    private sealed record ProposedDishDto(
        Guid RecipeId,
        int Servings,
        int Ordinal,
        RecipeScoreBreakdownDto? ScoreBreakdown);

    /// <summary>
    /// Explicit cache shape for server-owned objective evidence. Keeping it alongside the pending proposal
    /// makes review/reload faithful while still accepting older pending entries that have no breakdown.
    /// </summary>
    private sealed record RecipeScoreBreakdownDto(
        Guid RecipeId,
        decimal WeightedScore,
        decimal WasteScore,
        decimal CostScore,
        decimal VarietyScore,
        decimal WasteContribution,
        decimal CostContribution,
        decimal VarietyContribution,
        List<RecipeFacetContributionDto> VarietyContributions,
        RecipeTieBreakSignalsDto TieBreakSignals)
    {
        public static RecipeScoreBreakdownDto? FromDomain(RecipeScoreBreakdown? score) => score is null
            ? null
            : new RecipeScoreBreakdownDto(
                score.RecipeId,
                score.WeightedScore,
                score.WasteScore,
                score.CostScore,
                score.VarietyScore,
                score.WasteContribution,
                score.CostContribution,
                score.VarietyContribution,
                score.VarietyContributions.Select(RecipeFacetContributionDto.FromDomain).ToList(),
                RecipeTieBreakSignalsDto.FromDomain(score.TieBreakSignals));

        public RecipeScoreBreakdown ToDomain() => new(
            RecipeId,
            WeightedScore,
            WasteScore,
            CostScore,
            VarietyScore,
            WasteContribution,
            CostContribution,
            VarietyContribution,
            VarietyContributions.Select(contribution => contribution.ToDomain()).ToList(),
            TieBreakSignals.ToDomain());
    }

    private sealed record RecipeFacetContributionDto(
        RecipeDiversityFacet Facet,
        decimal MarginalScore,
        decimal PriorUse,
        RecipeDiversityConfidence Confidence,
        List<string> MatchedValues)
    {
        public static RecipeFacetContributionDto FromDomain(RecipeFacetContribution contribution) => new(
            contribution.Facet,
            contribution.MarginalScore,
            contribution.PriorUse,
            contribution.Confidence,
            [..contribution.MatchedValues]);

        public RecipeFacetContribution ToDomain() => new(
            Facet, MarginalScore, PriorUse, Confidence, MatchedValues);
    }

    private sealed record RecipeTieBreakSignalsDto(
        decimal PreferredTagSignal,
        decimal RatingSignal,
        int CostEvidenceRank,
        int WasteEvidenceRank)
    {
        public static RecipeTieBreakSignalsDto FromDomain(RecipeTieBreakSignals signals) => new(
            signals.PreferredTagSignal,
            signals.RatingSignal,
            signals.CostEvidenceRank,
            signals.WasteEvidenceRank);

        public RecipeTieBreakSignals ToDomain() => new(
            PreferredTagSignal, RatingSignal, CostEvidenceRank, WasteEvidenceRank);
    }
}
