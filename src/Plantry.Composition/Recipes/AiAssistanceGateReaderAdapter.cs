using Plantry.Identity.Application;
using Plantry.Recipes.Application;

namespace Plantry.Web.Recipes;

/// <summary>
/// Composition-side adapter for <see cref="IAiAssistanceGateReader"/> — delegates to Identity's
/// <see cref="IAiAssistanceGate"/>, the single source of truth for the per-household assistive-AI toggle
/// (plantry-qll2.1). Lives in Plantry.Composition, the composition root that references both contexts, so
/// the Recipes project stays <c>→ SharedKernel only</c> and never takes a hard dependency on Identity
/// (ADR-002). The recipe editor's tag-suggestion gate therefore resolves the exact same value the
/// /Settings/Ai toggle writes — mirroring <see cref="ExpiringSoonHorizonReaderAdapter"/>.
/// </summary>
public sealed class AiAssistanceGateReaderAdapter(IAiAssistanceGate gate)
    : IAiAssistanceGateReader
{
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        gate.IsEnabledAsync(ct);
}
