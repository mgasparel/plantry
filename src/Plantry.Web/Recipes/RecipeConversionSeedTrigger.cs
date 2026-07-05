using Plantry.Catalog.Infrastructure;
using Plantry.Inventory.Infrastructure;
using Plantry.Recipes.Application;
using Plantry.Recipes.Infrastructure;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Tenancy;
using Plantry.Web.Background;

namespace Plantry.Web.Recipes;

/// <summary>
/// Request-side entry point for the async recipe-conversion seed (plantry-qll2.4). The recipe editor hands
/// it the household plus the unit gaps a just-saved recipe left behind; it captures them into a
/// fire-and-forget background work item so the save response returns immediately (the LLM call never sits
/// in the request path). The work item runs later in its own DI scope with <b>tenancy armed exactly as
/// <c>RlsMiddleware</c> does</b> — there is no ambient HTTP request or scope in the background — then
/// delegates to <see cref="RecipeConversionSeeder"/>.
///
/// <para><see cref="Available"/> reflects whether a real inferrer is configured; the editor checks it
/// (together with the household AI toggle) before deferring a conversion, so it never saves a recipe with
/// a permanent gap that no seeder can fill — it falls back to the manual C10 prompt instead.</para>
/// </summary>
public sealed class RecipeConversionSeedTrigger(
    IBackgroundTaskQueue queue,
    IIngredientConversionInferrer inferrer,
    ILogger<RecipeConversionSeedTrigger> logger)
{
    /// <summary>True when a real (keyed) conversion inferrer is configured — see <see cref="IIngredientConversionInferrer.IsAvailable"/>.</summary>
    public bool Available => inferrer.IsAvailable;

    /// <summary>
    /// Queues an async seed for <paramref name="gaps"/> under <paramref name="household"/>. No-op when
    /// there are no gaps or no inferrer is available. Non-blocking: it enqueues and returns; the seed runs
    /// after the response.
    /// </summary>
    public async Task EnqueueAsync(
        HouseholdId household, IReadOnlyList<ConversionSeedRequest> gaps, CancellationToken ct = default)
    {
        if (!Available || gaps.Count == 0)
            return;

        var householdId = household.Value;

        await queue.EnqueueAsync(async (sp, workCt) =>
        {
            // Arm tenancy with no HTTP request, exactly as FlyerIngestionCycle does: the Postgres RLS GUC
            // plus the EF query filters. The seeder touches Catalog (products/conversions); the deferred
            // unit-gap follow-up below also reads Recipes (cook_consume_line) and drives an Inventory
            // consume, so all three query filters must be armed or the follow-up is a silent no-op / leak.
            sp.GetRequiredService<TenantContext>().Set(householdId);
            sp.GetRequiredService<CatalogDbContext>().SetHouseholdId(householdId);
            sp.GetRequiredService<RecipesDbContext>().SetHouseholdId(householdId);
            sp.GetRequiredService<InventoryDbContext>().SetHouseholdId(householdId);

            var seeder = sp.GetRequiredService<RecipeConversionSeeder>();
            await seeder.SeedAsync(gaps, workCt);

            // Composition-layer trigger for plantry-qll2.6: a conversion just landed for these products, so
            // retro-apply any DeferredUnitGap consume lines waiting on them (whatever their provenance — this
            // is the AI-seed path; the manual add/promote paths call ApplyDeferredUnitGaps directly). Cheap
            // and idempotent: a product with no deferred line, or whose seed was skipped/unfilled, is a no-op.
            var applier = sp.GetRequiredService<ApplyDeferredUnitGaps>();
            var productIds = gaps.Select(g => g.ProductId).Distinct().ToList();
            await applier.ExecuteAsync(productIds, workCt);
        }, ct);

        logger.LogInformation(
            "Queued async conversion seed for household {HouseholdId}: {GapCount} unit gap(s).",
            householdId, gaps.Count);
    }
}
