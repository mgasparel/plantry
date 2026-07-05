namespace Plantry.Recipes.Application;

/// <summary>
/// Untrusted-LLM port that infers a per-product density/cross-measure conversion factor for a single
/// ordered unit pair (plantry-qll2.4, ADR-022). When a recipe line is measured in a unit whose dimension
/// mismatches its product's stock unit — "1 cup cashews" against a product stocked in grams — and no
/// <c>ProductConversion</c> bridges the pair, the async post-save seeder asks this port for the factor
/// (e.g. "1 cup of cashews ≈ 120 g") and records it as an <c>ai_suggested</c> conversion the converter
/// can use immediately (ADR-022, provenance-blind). Precision is explicitly not a goal — a wrong-ish
/// density drifts pantry counts slightly and Take Stock reconciles; wrong-and-silent beats
/// correct-and-nagging (the ticket's stance).
///
/// <para>The AI is an untrusted external function (ADR-007 / Gate 5): this returns a <b>provisional
/// reference value</b>, never a state change, and every failure path — API error, empty/malformed
/// response, out-of-range factor — is a <em>soft</em> failure that returns <c>null</c> (no seed) and
/// never throws into the caller. A null result simply leaves today's unit-gap behaviour in place; the
/// pair may be retried on the next save of any recipe with the same mismatch.</para>
///
/// <para>Defined here in Recipes.Application and implemented in Plantry.Recipes.Infrastructure over the
/// shared <c>AiOptions</c>/<c>ChatClient</c> (no model-tier concept exists — ADR-007 leaves per-task
/// model selection open, so the single <c>AiOptions.Model</c> is used as-is). A keyless dev/E2E host
/// registers a disabled no-op implementation, which is why <see cref="IsAvailable"/> exists — the editor
/// consults it to decide whether to defer a conversion to the AI at all, falling back to the manual
/// C10 prompt when the seeder cannot run.</para>
/// </summary>
public interface IIngredientConversionInferrer
{
    /// <summary>
    /// True when a real inferrer is configured (an <c>AI:ApiKey</c> is present). False for the disabled
    /// no-op — the recipe editor uses this to decide whether to defer the missing conversion to the AI
    /// (save with a unit-gap) or fall back to the synchronous C10 conversion prompt. Constant for the
    /// lifetime of the process; it reflects startup configuration, not the per-household toggle.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Infers the factor that converts one <paramref name="fromUnitCode"/> of
    /// <paramref name="productName"/> into <paramref name="toUnitCode"/> (i.e. how many
    /// <paramref name="toUnitCode"/> equal one <paramref name="fromUnitCode"/>), matching
    /// <c>UnitConverter</c>'s <c>factor</c> semantics for the ordered pair. Returns <c>null</c> on any
    /// soft failure or when the model gives no usable, positive, finite number.
    /// </summary>
    Task<decimal?> InferFactorAsync(
        string productName, string fromUnitCode, string toUnitCode, CancellationToken ct = default);
}
