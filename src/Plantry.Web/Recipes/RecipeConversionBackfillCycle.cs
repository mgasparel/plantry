using Plantry.Catalog.Infrastructure;
using Plantry.Identity.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;

namespace Plantry.Web.Recipes;

/// <summary>
/// The one-shot rollout backfill for AI-suggested recipe conversions (plantry-qll2.4). Scans existing
/// recipes for cross-dimension unit gaps that no <c>ProductConversion</c> bridges and seeds an
/// <c>ai_suggested</c> factor for each, through the very same <see cref="RecipeConversionSeeder"/> the
/// async post-save trigger uses — so a recipe authored before this feature shipped catches up.
///
/// <para>Deliberately kept OUT of the normal recipe-save request path (the ticket's explicit constraint):
/// it is <b>not</b> a <see cref="BackgroundService"/>, never runs at boot, and is exposed only through a
/// dev-only manual endpoint (mirroring <c>/Dev/Deals/PullNow</c> and the DM-16 purchase-store backfill).
/// It reproduces <c>RlsMiddleware</c>'s tenancy arming with no HTTP request — the same cross-tenant
/// structure as <c>FlyerIngestionCycle</c> — so it covers every household. Idempotent: the seeder skips
/// any pair already bridged (of any provenance), so re-running is safe and cheap.</para>
/// </summary>
public sealed class RecipeConversionBackfillCycle(
    IServiceScopeFactory scopeFactory, ILogger<RecipeConversionBackfillCycle> logger)
{
    /// <summary>Sweeps every household, isolating a per-household failure so one bad household never aborts the sweep.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HouseholdId> households;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            // No TenantContext armed → app.household_id unset → the identity.households RLS policy exposes
            // all rows (the pre-auth carve-out). This is the sole cross-tenant read in the sweep.
            var repo = scope.ServiceProvider.GetRequiredService<IHouseholdRepository>();
            households = await repo.ListAllIdsAsync(ct);
        }

        logger.LogInformation("Recipe conversion backfill starting for {HouseholdCount} household(s).", households.Count);

        foreach (var household in households)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForHouseholdAsync(household, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Recipe conversion backfill failed for household {HouseholdId}; continuing to the next.", household.Value);
            }
        }
    }

    /// <summary>
    /// Processes one household in a fresh scope with tenancy armed exactly as <c>RlsMiddleware</c> does:
    /// <see cref="TenantContext"/> (arms the Postgres GUC via the connection interceptor) plus
    /// <c>SetHouseholdId</c> on Recipes (the recipe read) and Catalog (product/unit resolution + the
    /// conversion write). Both EF contexts must be armed or the sweep is a cross-household leak (or a silent
    /// no-op) — hence both, every household.
    /// </summary>
    public async Task<int> RunForHouseholdAsync(HouseholdId household, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var id = household.Value;
        sp.GetRequiredService<TenantContext>().Set(id);                // arms Postgres RLS (app.household_id GUC)
        sp.GetRequiredService<RecipesDbContext>().SetHouseholdId(id);  // Recipes: the recipe/ingredient read
        sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(id);  // Catalog: product/unit reads + conversion write

        var recipes = sp.GetRequiredService<IRecipeRepository>();
        var products = sp.GetRequiredService<ICatalogProductReader>();
        var seeder = sp.GetRequiredService<RecipeConversionSeeder>();

        var allRecipes = await recipes.ListForBrowseAsync(ct);
        if (allRecipes.Count == 0) return 0;

        var requests = await GatherGapsAsync(allRecipes, products, ct);
        if (requests.Count == 0) return 0;

        var seeded = await seeder.SeedAsync(requests, ct);
        logger.LogInformation(
            "Recipe conversion backfill for household {HouseholdId}: {GapCount} candidate gap(s), {SeededCount} seeded.",
            id, requests.Count, seeded);
        return seeded;
    }

    /// <summary>
    /// Collects the cross-dimension unit gaps across a household's recipes: for each tracked ingredient
    /// line whose unit's dimension differs from its product's stock (default) unit, a
    /// <see cref="ConversionSeedRequest"/> (line unit → stock unit). Same-dimension lines (which the
    /// converter bridges universally) are ignored. The seeder de-duplicates and re-checks existing bridges,
    /// so this only needs to find candidates, not filter already-resolved ones.
    /// </summary>
    private static async Task<IReadOnlyList<ConversionSeedRequest>> GatherGapsAsync(
        IReadOnlyList<Recipe> allRecipes, ICatalogProductReader products, CancellationToken ct)
    {
        // Distinct products referenced by a tracked, unit-bearing ingredient line.
        var productIds = allRecipes
            .SelectMany(r => r.Ingredients)
            .Where(i => i.UnitId.HasValue)
            .Select(i => i.ProductId)
            .Distinct()
            .ToList();
        if (productIds.Count == 0) return [];

        var units = await products.ListUnitsAsync(ct);
        var unitById = units.ToDictionary(u => u.Id);

        // Resolve each product's name + default (stock) unit. FindAsync is per-product, acceptable for a
        // one-shot dev/migration sweep over a small self-curated corpus.
        var productInfo = new Dictionary<Guid, (string Name, Guid DefaultUnitId)>();
        foreach (var pid in productIds)
        {
            var product = await products.FindAsync(pid, ct);
            if (product is { TrackStock: true })
                productInfo[pid] = (product.Name, product.DefaultUnitId);
        }

        var seen = new HashSet<(Guid, Guid, Guid)>();
        var requests = new List<ConversionSeedRequest>();
        foreach (var ingredient in allRecipes.SelectMany(r => r.Ingredients))
        {
            if (ingredient.UnitId is not { } fromUnitId) continue;
            if (!productInfo.TryGetValue(ingredient.ProductId, out var info)) continue;

            var toUnitId = info.DefaultUnitId;
            if (fromUnitId == toUnitId) continue;
            if (!unitById.TryGetValue(fromUnitId, out var fromUnit)) continue;
            if (!unitById.TryGetValue(toUnitId, out var toUnit)) continue;

            // Cross-dimension only — a same-dimension pair needs no product-specific density factor.
            if (!string.IsNullOrEmpty(fromUnit.Dimension)
                && string.Equals(fromUnit.Dimension, toUnit.Dimension, StringComparison.Ordinal))
                continue;

            if (!seen.Add((ingredient.ProductId, fromUnitId, toUnitId))) continue;

            requests.Add(new ConversionSeedRequest(
                ingredient.ProductId, info.Name, fromUnitId, fromUnit.Code, toUnitId, toUnit.Code));
        }

        return requests;
    }
}
