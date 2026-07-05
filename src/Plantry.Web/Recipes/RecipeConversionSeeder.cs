using Plantry.Catalog.Domain;
using Plantry.Recipes.Application;
using Plantry.SharedKernel.Domain;

namespace Plantry.Web.Recipes;

/// <summary>
/// One recipe unit-gap to resolve into an AI-suggested conversion (plantry-qll2.4). The ordered pair is
/// the recipe line's unit (<see cref="FromUnitId"/>/<see cref="FromUnitCode"/>) → the product's stock
/// unit (<see cref="ToUnitId"/>/<see cref="ToUnitCode"/>), matching <c>UnitConverter</c>'s factor
/// direction; the unit codes ride along so the seeder can build the LLM prompt without a re-read.
/// </summary>
public sealed record ConversionSeedRequest(
    Guid ProductId,
    string ProductName,
    Guid FromUnitId,
    string FromUnitCode,
    Guid ToUnitId,
    string ToUnitCode);

/// <summary>
/// The async seeding worker behind the recipe-save unit-conversion trigger (plantry-qll2.4, ADR-022).
/// Runs inside a background DI scope (dispatched via <see cref="Plantry.Web.Background.IBackgroundTaskQueue"/>
/// after the save response, or synchronously by the one-shot backfill) with tenancy already armed by the
/// caller. For each requested unit gap it:
/// <list type="number">
///   <item>re-loads the product with its conversions and <b>re-checks the ordered pair</b> — if a
///   conversion already bridges it (any provenance: a sibling recipe seeded it, or the user added/promoted
///   one between save and this run), it skips with <b>no LLM call</b> (the ticket's "no call when a bridge
///   already exists" guard, robust to the save→seed race);</item>
///   <item>otherwise asks <see cref="IIngredientConversionInferrer"/> for the factor and, on a usable
///   value, records it via <see cref="Product.AddConversion"/> with
///   <see cref="ConversionSource.AiSuggested"/> — computationally live immediately and promotable on the
///   product page (ADR-022). The aggregate's own idempotent merge rule is a second line of defence
///   against duplicates.</item>
/// </list>
/// Best-effort throughout: an unknown product, a null/soft-failed inference, or an equal pair is logged
/// and skipped, never thrown — a failed seed simply leaves today's unit-gap behaviour in place. Mirrors
/// the intake <c>SeedConversionAdapter</c> seam (Catalog accessed directly so Recipes stays off Catalog's
/// EF context, ADR-010).
/// </summary>
public sealed class RecipeConversionSeeder(
    IProductRepository products,
    IIngredientConversionInferrer inferrer,
    IClock clock,
    ILogger<RecipeConversionSeeder> logger)
{
    /// <summary>
    /// Resolves each requested unit gap (see the class summary). Returns the number of conversions
    /// actually seeded — used by the backfill for a summary count; the async post-save trigger ignores it.
    /// </summary>
    public async Task<int> SeedAsync(IReadOnlyList<ConversionSeedRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0) return 0;

        // De-duplicate by (product, from, to): a recipe can list the same product/pair on two lines, and
        // the backfill can gather the same gap from many recipes.
        var distinct = requests
            .GroupBy(r => (r.ProductId, r.FromUnitId, r.ToUnitId))
            .Select(g => g.First())
            .ToList();

        var productIds = distinct.Select(r => ProductId.From(r.ProductId)).Distinct().ToList();
        var loaded = await products.ListWithConversionsAsync(productIds, ct);
        var byId = loaded.ToDictionary(p => p.Id);

        var seeded = 0;
        foreach (var req in distinct)
        {
            ct.ThrowIfCancellationRequested();

            if (req.FromUnitId == req.ToUnitId)
                continue; // a same-unit "gap" is nonsensical — never seed it.

            if (!byId.TryGetValue(ProductId.From(req.ProductId), out var product))
            {
                logger.LogWarning(
                    "Skipping conversion seed: product {ProductId} not found in this household.", req.ProductId);
                continue;
            }

            var fromUnit = UnitId.From(req.FromUnitId);
            var toUnit = UnitId.From(req.ToUnitId);

            // Re-check the bridge at seed time — a conversion for the exact ordered pair may have landed
            // since the recipe saved (sibling seed, or a user add/promote). No call when one exists.
            var alreadyBridged = product.Conversions.Any(c => c.FromUnitId == fromUnit && c.ToUnitId == toUnit);
            if (alreadyBridged)
            {
                logger.LogInformation(
                    "Skipping conversion seed for product {ProductId}: {From}→{To} already bridged.",
                    req.ProductId, req.FromUnitCode, req.ToUnitCode);
                continue;
            }

            var factor = await inferrer.InferFactorAsync(req.ProductName, req.FromUnitCode, req.ToUnitCode, ct);
            if (factor is not { } f || f <= 0m)
            {
                logger.LogInformation(
                    "No AI conversion seeded for product {ProductId} ({From}→{To}): inference returned no usable factor.",
                    req.ProductId, req.FromUnitCode, req.ToUnitCode);
                continue;
            }

            product.AddConversion(fromUnit, toUnit, f, clock, ConversionSource.AiSuggested);
            // Commit per aggregate (Gate 2 — one aggregate root per transaction), mirroring the intake
            // SeedConversionAdapter which saves one product per call. A recipe (or the backfill) with gaps
            // on several products must not commit them all in one cross-aggregate save.
            await products.SaveChangesAsync(ct);
            seeded++;
            logger.LogInformation(
                "Seeded AI-suggested conversion for product {ProductId}: {From}→{To} factor {Factor}.",
                req.ProductId, req.FromUnitCode, req.ToUnitCode, f);
        }

        return seeded;
    }
}
