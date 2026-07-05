namespace Plantry.Recipes.Application;

/// <summary>
/// Anti-corruption read port onto the household's assistive-AI gate (owned by Identity,
/// <c>IAiAssistanceGate</c> — the single point of truth for whether the current household has the
/// assistive-AI class enabled, plantry-qll2.1). Lets the Recipes context short-circuit its edit-moment
/// AI features (tag suggestions today; the contradiction nudge and unit-conversion resolution as the
/// sibling qll2.3/qll2.4 tickets land) against the <b>same</b> household toggle the /Settings/Ai page
/// writes, without coupling Recipes to the Identity domain model or its EF context (ADR-002). Defined
/// here in Recipes.Application and <b>implemented in Plantry.Composition</b> over Identity's
/// <c>IAiAssistanceGate</c>, so the Recipes project keeps its <c>→ SharedKernel only</c> dependency,
/// mirroring <see cref="IExpiringSoonHorizonReader"/>.
///
/// <para>Shared across the qll2 edit-moment AI features: this is the reader-port the epic's design
/// (plantry-qll2) calls for. Whichever of qll2.2/qll2.3/qll2.4 landed first built it; the others
/// consume it rather than re-declaring their own copy.</para>
/// </summary>
public interface IAiAssistanceGateReader
{
    /// <summary>
    /// True when the current household has assistive AI enabled. Fails open to <c>true</c> (the
    /// assistive class is opt-out, not opt-in) when there is no household in context or no persisted
    /// household row yet — matching the Identity gate's own fail-open contract.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
}
