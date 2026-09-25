using Plantry.Pantry.Application;
using Plantry.Pantry.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using Plantry.Planning.Application;

namespace Plantry.Web.Shopping;

/// <summary>
/// Web-layer adapter implementing <see cref="IShoppingPantryReader"/> over the Inventory
/// bounded context's persistence and catalog read facade. This is the anti-corruption layer
/// seam between Shopping and Inventory — Shopping never takes a direct dependency on Inventory's
/// EF context or repositories (ADR-002). Follows the same adapter pattern as
/// <c>InventoryStockReaderAdapter</c> (Recipes → Inventory ACL).
///
/// <para>On-hand numbers (leaf and parent alike) come from the single shared
/// <see cref="IOnHandRollupQuery"/> (plantry-oh27.2/oh27.3) — this adapter no longer runs its own
/// <c>InventoryQueryService.DisplayQuantity</c> aggregation, so the pantry list, the parent detail
/// page, and this Shopping ACL adapter can never disagree about the same on-hand data.
/// <see cref="IProductStockRepository.ListForHouseholdAsync"/> is still called, but only to know which
/// leaf ids actually carry a stock row (preserving <see cref="GetStockLevelsAsync"/>'s "never-stocked
/// leaf is omitted" contract) — never to re-derive quantities.</para>
///
/// <para><b>Parent fold (plantry-oh27.3):</b> a variant appears in restock candidates only through
/// its OWN <see cref="LowStockRule"/>; otherwise its PARENT represents it — <see cref="IOnHandRollupQuery"/>
/// supplies the parent's aggregate on-hand (Σ live variants, converted to the parent's default unit).
/// A leaf with no parent is unaffected. See <see cref="GetLowStockProductsAsync"/> and
/// <see cref="GetFrequentStapleProductsAsync"/> for the concrete tiering.</para>
/// </summary>
public sealed class ShoppingPantryReaderAdapter(
    IProductStockRepository stocks,
    ILowStockRuleRepository rules,
    ICatalogReadFacade catalog,
    IOnHandRollupQuery onHandRollup,
    ITenantContext tenant)
    : IShoppingPantryReader
{
    public async Task<IReadOnlyDictionary<Guid, ShoppingPantryStockLevel>> GetStockLevelsAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ShoppingPantryStockLevel>();

        if (tenant.HouseholdId is not { } householdGuid)
            return new Dictionary<Guid, ShoppingPantryStockLevel>();

        var householdId = HouseholdId.From(householdGuid);
        var distinctIds = productIds.Distinct().ToList();

        // Only used to preserve the pre-existing "a never-stocked LEAF is omitted" contract (see the
        // port doc) — IOnHandRollupQuery.ForProductsAsync always resolves a directly-requested leaf
        // (rule 3 of plantry-oh27.2, ZeroLeafLevel), so a plain HashSet of ids that actually carry a
        // ProductStock row is enough to tell "never stocked, omit" apart from "has a row, OnHand may be
        // zero, include". A PARENT never holds stock at all (epic constraint) so this check never applies
        // to it — every parent entry from the rollup below is kept.
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        var stockedIds = allStock.Select(s => s.ProductId).ToHashSet();

        var rulesByProduct = await rules.ListForHouseholdAsync(householdId, ct);

        // Single shared on-hand source for both leaf and parent (plantry-oh27.2/oh27.3) — no second
        // DisplayQuantity-based aggregation path.
        var rollup = await onHandRollup.ForProductsAsync(distinctIds, ct);

        var result = new Dictionary<Guid, ShoppingPantryStockLevel>();
        foreach (var (productId, level) in rollup)
        {
            if (!level.IsParent && !stockedIds.Contains(productId))
                continue; // a genuinely never-stocked leaf stays omitted, matching the port's contract

            var hasRule = rulesByProduct.TryGetValue(productId, out var rule);
            var isLow = level.OnHand > 0m && hasRule && rule!.IsRunningLow(level.OnHand);
            result[productId] = new ShoppingPantryStockLevel(
                ProductId: productId,
                OnHand: level.OnHand,
                UnitCode: level.UnitCode,
                IsLow: isLow,
                HasLowStockThreshold: hasRule,
                IsParent: level.IsParent);
        }

        return result;
    }

    /// <inheritdoc cref="IShoppingPantryReader.GetLowStockProductsAsync"/>
    public async Task<IReadOnlyList<ShoppingPantryStockLevel>> GetLowStockProductsAsync(
        CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return [];

        var householdId = HouseholdId.From(householdGuid);

        // Household-wide discovery scan: every leaf with a stock row, plus every parent with at least
        // one live variant that has a stock row (IOnHandRollupQuery.ForHouseholdAsync's own contract).
        var rollup = await onHandRollup.ForHouseholdAsync(ct);
        if (rollup.Count == 0)
            return [];

        var rulesByProduct = await rules.ListForHouseholdAsync(householdId, ct);
        var catalogProducts = await catalog.ListProductsAsync(ct);
        var catalogByProduct = catalogProducts.ToDictionary(p => p.Id);

        var result = new List<ShoppingPantryStockLevel>();

        foreach (var (productId, level) in rollup)
        {
            if (!catalogByProduct.TryGetValue(productId, out var product))
                continue; // product no longer in catalog — skip

            // excludeProduced: a produced product (recipe yield / cook leftover, plantry-sn6v) is
            // never a restock candidate by definition — "made at home, not bought" — regardless of
            // how low or out it reads.
            if (product.IsProduced)
                continue;

            if (!level.IsParent && product.ParentProductId is not null)
            {
                // A variant appears in restock candidates only through its OWN rule (epic rule 3) —
                // without one it is represented by its parent's entry (handled in the `level.IsParent`
                // branch below) and must not also surface itself.
                if (!rulesByProduct.ContainsKey(productId))
                    continue;
            }

            var hasRule = rulesByProduct.TryGetValue(productId, out var rule);

            // IsLow means "running low" only: a positive but low quantity, 0 < onHand ≤ threshold (per
            // LowStockRule.IsRunningLow, plantry-oh27.1) — deliberately false when out so the Shopping
            // subline renders out and low as distinct, mutually-exclusive states. A parent/leaf with no
            // rule can still be a Tier-3 "out" candidate (onHand ≤ 0) even though IsLow stays false.
            var isLow = level.OnHand > 0m && hasRule && rule!.IsRunningLow(level.OnHand);
            if (!isLow && level.OnHand > 0m)
                continue; // in-stock and not low — not a restock candidate

            if (level.IsParent && level.OnHand <= 0m && level.UnconvertedVariantIds.Count > 0)
                continue; // every contributing variant failed conversion into the parent unit — unknown, not "out" (mirrors the plantry-2hfi leaf invariant below)

            result.Add(new ShoppingPantryStockLevel(
                ProductId: productId,
                OnHand: level.OnHand,
                UnitCode: level.UnitCode,
                IsLow: isLow,
                HasLowStockThreshold: hasRule,
                IsParent: level.IsParent));
        }

        return result;
    }

    public async Task<IReadOnlyList<ShoppingPantryStockLevel>> GetFrequentStapleProductsAsync(
        DateOnly today, CancellationToken ct = default)
    {
        if (tenant.HouseholdId is not { } householdGuid)
            return [];

        var householdId = HouseholdId.From(householdGuid);
        var allStock = await stocks.ListForHouseholdAsync(householdId, ct);
        if (allStock.Count == 0)
            return [];

        var rulesByProduct = await rules.ListForHouseholdAsync(householdId, ct);
        var catalogProducts = await catalog.ListProductsAsync(ct);
        var catalogByProduct = catalogProducts.ToDictionary(p => p.Id);

        // Group stock rows by (parent id ?? own id) — EXCEPT a row whose own product already has a
        // rule stays its own group (GroupKey below), since a variant/leaf with its own rule is never
        // folded into its parent (epic rule 3). A group is a Tier-2 candidate only if the group's own
        // product (parent or leaf) has no rule and the UNION of the group's purchase dates is frequent.
        var groups = allStock
            .Where(s => catalogByProduct.TryGetValue(s.ProductId, out var p) && !p.IsProduced)
            .GroupBy(s => GroupKey(s.ProductId, catalogByProduct, rulesByProduct));

        var candidateGroupIds = new List<Guid>();
        foreach (var group in groups)
        {
            var groupId = group.Key;
            if (rulesByProduct.ContainsKey(groupId))
                continue; // the group's own product (parent or leaf) already has a rule — Tier 1 territory
            if (!catalogByProduct.TryGetValue(groupId, out var groupProduct))
                continue; // group resolves to a parent no longer in the catalog — skip
            if (groupProduct.IsProduced)
                continue; // made at home, not bought — never a restock candidate (plantry-sn6v), same rule GetLowStockProductsAsync applies to its rows

            var dates = group.SelectMany(s => s.Entries.Select(e => e.PurchasedAt));
            if (FrequentStaplePredicate.IsFrequent(dates, today))
                candidateGroupIds.Add(groupId);
        }

        if (candidateGroupIds.Count == 0)
            return [];

        // On-hand for each candidate group's representative id — IOnHandRollupQuery resolves a leaf's
        // own figure or a parent's Σ-of-live-variants figure through the same shared rollup (plantry-oh27.2).
        var levels = await onHandRollup.ForProductsAsync(candidateGroupIds, ct);
        var result = new List<ShoppingPantryStockLevel>(candidateGroupIds.Count);
        foreach (var groupId in candidateGroupIds)
        {
            if (!levels.TryGetValue(groupId, out var level))
                continue;
            result.Add(new ShoppingPantryStockLevel(
                ProductId: groupId,
                OnHand: level.OnHand,
                UnitCode: level.UnitCode,
                IsLow: false, // Tier 2 candidates have no threshold by construction — never "running low"
                HasLowStockThreshold: false,
                IsParent: level.IsParent));
        }
        return result;
    }

    /// <summary>Group key for the staple fold: a product with its own <see cref="LowStockRule"/> stays
    /// its own group (it is filtered out immediately by the caller since it has a rule — Tier 1
    /// territory, not Tier 2); otherwise a variant's group is its parent, and a parentless product's
    /// group is itself.</summary>
    private static Guid GroupKey(
        Guid productId,
        Dictionary<Guid, CatalogProductInfo> catalogByProduct,
        IReadOnlyDictionary<Guid, LowStockRule> rulesByProduct) =>
        rulesByProduct.ContainsKey(productId)
            ? productId
            : catalogByProduct.TryGetValue(productId, out var product) && product.ParentProductId is { } parentId
                ? parentId
                : productId;
}
