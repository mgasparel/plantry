using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Shopping.Application;
using Plantry.Shopping.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Deterministic fixture data for the L4 Shopping page snapshot tests (P2-Sc).
/// Three items exercise all rendering paths:
/// <list type="bullet">
///   <item>A product-backed item "Milk" in category "Dairy" — unchecked.</item>
///   <item>A free-text item "Sriracha" — unchecked, falls in Uncategorized.</item>
///   <item>A product-backed item "Flour" in category "Baking" — checked, sinks to bottom.</item>
/// </list>
/// GUIDs are scrubbed by Verify's ScrubInlineGuids() so random ids do not defeat the baselines.
/// </summary>
public static class ShoppingListFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdAId);
    private static readonly IClock Clock = SystemClock.Instance;

    // Fixed product ids for deterministic catalog map
    public static readonly Guid MilkProductId  = Guid.Parse("33333333-3333-3333-3333-333333333301");
    public static readonly Guid FlourProductId = Guid.Parse("33333333-3333-3333-3333-333333333302");
    public static readonly Guid UnitId         = Guid.Parse("33333333-3333-3333-3333-333333333310");

    public static ShoppingList BuildList()
    {
        var list = ShoppingList.Create(Household, Clock);

        // Unchecked product-backed item: Milk (Dairy category)
        list.AddItem(MilkProductId, quantity: 2m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);

        // Unchecked free-text item: Sriracha (no category → Uncategorized)
        list.AddFreeTextItem("Sriracha", quantity: null, unitId: null, note: null, Clock);

        // Checked item: Flour (Baking category) — sinks to bottom
        var flourItem = list.AddItem(FlourProductId, quantity: 500m, unitId: UnitId, note: null,
            source: ItemSource.Manual, sourceRef: null, Clock);
        list.CheckOff(flourItem.Id, userId: Guid.Parse("00000000-0000-0000-0000-0000000000aa"), Clock);

        return list;
    }

    public static IReadOnlyDictionary<Guid, ShoppingProductSummary> ProductSummaries() =>
        new Dictionary<Guid, ShoppingProductSummary>
        {
            [MilkProductId]  = new(MilkProductId,  "Milk",  "Dairy"),
            [FlourProductId] = new(FlourProductId, "Flour", "Baking"),
        };

    public static IReadOnlyDictionary<Guid, string> UnitCodes() =>
        new Dictionary<Guid, string>
        {
            [UnitId] = "L",
        };

    public static IReadOnlyList<ShoppingProductCandidate> ProductCandidates() =>
    [
        new(MilkProductId, "Milk"),
        new(FlourProductId, "Flour"),
    ];
}

// ── Shopping page fakes ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory <see cref="IShoppingListRepository"/> for the Shopping L4 tests.
/// Tenant-scoped: only returns the seeded list for the owning household (mirrors EF query filter + RLS).
/// </summary>
public sealed class FakeShoppingRepository(ITenantContext tenant, ShoppingList list)
    : IShoppingListRepository
{
    public Task<ShoppingList?> GetForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        tenant.HouseholdId is { } hid && list.HouseholdId.Value == hid
            ? Task.FromResult<ShoppingList?>(list)
            : Task.FromResult<ShoppingList?>(null);

    public Task<ShoppingList?> GetByIdAsync(ShoppingListId id, CancellationToken ct = default) =>
        Task.FromResult(list.Id == id ? (ShoppingList?)list : null);

    public Task AddAsync(ShoppingList l, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// In-memory <see cref="IShoppingCatalogReader"/> for the Shopping L4 tests.
/// Returns the fixture product summaries and unit codes.
/// </summary>
public sealed class FakeShoppingCatalogReader(
    IReadOnlyDictionary<Guid, ShoppingProductSummary> summaries,
    IReadOnlyDictionary<Guid, string> unitCodes,
    IReadOnlyList<ShoppingProductCandidate> candidates)
    : IShoppingCatalogReader
{
    public Task<IReadOnlyDictionary<Guid, ShoppingProductSummary>> ResolveSummariesAsync(
        IReadOnlyList<Guid> productIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, ShoppingProductSummary> result = productIds
            .Where(summaries.ContainsKey)
            .ToDictionary(id => id, id => summaries[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, string>> ResolveUnitCodesAsync(
        IReadOnlyList<Guid> unitIds, CancellationToken ct = default)
    {
        IReadOnlyDictionary<Guid, string> result = unitIds
            .Where(unitCodes.ContainsKey)
            .ToDictionary(id => id, id => unitCodes[id]);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ShoppingProductCandidate>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult(candidates);

    /// <summary>Web-layer fake: conversion is not exercised in snapshot tests; always returns null.</summary>
    public Task<decimal?> TryConvertAsync(decimal amount, Guid fromUnitId, Guid toUnitId, Guid productId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(null);
}
