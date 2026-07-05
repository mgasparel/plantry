namespace Plantry.Identity.Application;

/// <summary>
/// The single point of truth for whether the current household's assistive-AI features are enabled.
///
/// Every governed call site — recipe tag suggestions, the diet-tag contradiction nudge, and
/// unit-conversion resolution (the "provisional value" AI class, plantry-qll2.1) — queries THIS,
/// rather than reading a household flag ad hoc. Sibling features in other bounded contexts consume
/// it through their own ACL reader port + a Composition adapter (mirroring the
/// <c>IExpiringSoonHorizon</c> pattern), so no downstream context takes a hard dependency on Identity.
///
/// Receipt parsing is deliberately NOT gated here: it is pipeline AI with no manual fallback, so the
/// user controls that cost by choosing whether to scan. The read-only gate lives here; the write path
/// (the Settings toggle) is on <see cref="AiAssistanceSettingsService"/>.
/// </summary>
public interface IAiAssistanceGate
{
    /// <summary>
    /// True when the current household has assistive AI enabled. Fails open to <c>true</c> (the
    /// assistive class is opt-out, not opt-in) when there is no household in context or no persisted
    /// household row yet.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
}
