using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.Planning.Infrastructure;
using Xunit;

namespace Plantry.Tests.Integration.MealPlanning;

/// <summary>L2 tests for <see cref="DistributedCacheProposalRejectionMemory"/> (plantry-l17y).</summary>
public sealed class DistributedCacheProposalRejectionMemoryTests
{
    private const string StoreKey = "household_20260615_session";
    private static readonly DateOnly Monday = new(2026, 6, 15);
    private static readonly MealSlotId SlotA = new(Guid.Parse("0193b4a0-5555-7000-8000-00000000000a"));
    private static readonly MealSlotId SlotB = new(Guid.Parse("0193b4a0-5555-7000-8000-00000000000b"));
    private static readonly Guid RecipeOne = Guid.Parse("0193b4a0-6666-7000-8000-000000000001");
    private static readonly Guid RecipeTwo = Guid.Parse("0193b4a0-6666-7000-8000-000000000002");

    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static DistributedCacheProposalRejectionMemory NewMemory(IDistributedCache cache) =>
        new(cache, NullLogger<DistributedCacheProposalRejectionMemory>.Instance);

    private static ProposedMeal Proposal(DateOnly date, MealSlotId slot, Guid recipeId) =>
        new(date, slot, [], [new ProposedDish(recipeId, 4, 1)], "test");

    [Fact(DisplayName = "RememberAsync_SameCellTwice_MergesAndDeduplicates")]
    public async Task RememberAsync_SameCellTwice_MergesAndDeduplicates()
    {
        var memory = NewMemory(NewCache());

        await memory.RememberAsync(StoreKey, [Proposal(Monday, SlotA, RecipeOne)]);
        await memory.RememberAsync(StoreKey, [Proposal(Monday, SlotA, RecipeOne), Proposal(Monday, SlotA, RecipeTwo)]);

        var result = await memory.GetAsync(StoreKey);
        var cell = Assert.Single(result);
        Assert.Equal(IProposalRejectionMemory.CellKey(Monday, SlotA), cell.Key);
        Assert.Equal(new HashSet<Guid> { RecipeOne, RecipeTwo }, cell.Value.ToHashSet());
    }

    [Fact(DisplayName = "RememberAsync_DifferentCells_StoredUnderSeparateKeys")]
    public async Task RememberAsync_DifferentCells_StoredUnderSeparateKeys()
    {
        var memory = NewMemory(NewCache());

        await memory.RememberAsync(StoreKey, [Proposal(Monday, SlotA, RecipeOne), Proposal(Monday, SlotB, RecipeTwo)]);

        var result = await memory.GetAsync(StoreKey);
        Assert.Equal(2, result.Count);
        Assert.Equal(new HashSet<Guid> { RecipeOne }, result[IProposalRejectionMemory.CellKey(Monday, SlotA)].ToHashSet());
        Assert.Equal(new HashSet<Guid> { RecipeTwo }, result[IProposalRejectionMemory.CellKey(Monday, SlotB)].ToHashSet());
    }

    [Fact(DisplayName = "RememberAsync_EmptySequence_StoresNothing")]
    public async Task RememberAsync_EmptySequence_StoresNothing()
    {
        var cache = NewCache();
        var memory = NewMemory(cache);

        await memory.RememberAsync(StoreKey, []);

        Assert.Empty(await memory.GetAsync(StoreKey));
        Assert.Null(await cache.GetStringAsync($"{StoreKey}:rejected"));
    }

    [Fact(DisplayName = "RejectionMemory_AndPendingStore_DoNotCollide")]
    public async Task RejectionMemory_AndPendingStore_DoNotCollide()
    {
        var cache = NewCache();
        var memory = NewMemory(cache);
        var pending = new DistributedCachePendingProposalStore(cache);

        await pending.SetAsync(StoreKey, [Proposal(Monday, SlotA, RecipeOne)]);
        Assert.Empty(await memory.GetAsync(StoreKey));

        await memory.RememberAsync(StoreKey, [Proposal(Monday, SlotB, RecipeTwo)]);
        var stillPending = Assert.Single(await pending.GetAsync(StoreKey));
        Assert.Equal(SlotA, stillPending.MealSlotId);
        Assert.Equal(RecipeOne, stillPending.Dishes[0].RecipeId);
    }

    [Fact(DisplayName = "GetAsync_CorruptPayload_ReturnsEmptyWithoutThrowing")]
    public async Task GetAsync_CorruptPayload_ReturnsEmptyWithoutThrowing()
    {
        var cache = NewCache();
        await cache.SetStringAsync($"{StoreKey}:rejected", "not json");
        var memory = NewMemory(cache);

        Assert.Empty(await memory.GetAsync(StoreKey));
    }
}
