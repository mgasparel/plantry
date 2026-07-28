using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Identity.Domain;

public sealed class Household : AggregateRoot<HouseholdId>
{
    public string Name { get; private set; } = string.Empty;
    public string? EmailIntakeAddress { get; private set; }
    public int ExpiryWarningDays { get; private set; } = 3;
    public string Theme { get; private set; } = "slate";

    /// <summary>
    /// Whether the household's assistive-AI features are enabled. Governs the "provisional value"
    /// AI class — recipe tag suggestions, the diet-tag contradiction nudge, and unit-conversion
    /// resolution (weight-to-volume, weight-to-each) — i.e. every AI call the feature still functions
    /// without (the manual path always remains). It deliberately does NOT govern receipt parsing: the
    /// two-stage intake pipeline IS the feature and has no non-AI fallback, so the user controls that
    /// cost by choosing whether to scan. A single household-wide switch (plantry-qll2.1); reads flow
    /// through <c>IAiAssistanceGate</c> so call sites never touch this property directly. Defaults ON.
    /// </summary>
    public bool AiAssistanceEnabled { get; private set; } = true;

    /// <summary>
    /// The household's display currency as an ISO 4217 alphabetic code (plantry-2x6e.1). Governs which
    /// currency freshly-written money adopts — budget writes stamp it, and the presentation edge labels
    /// bare-decimal money with it. A change never rewrites already-stored <see cref="Money"/> values:
    /// each keeps its own currency until re-saved. Lives on the aggregate (like <c>Theme</c> and
    /// <c>AiAssistanceEnabled</c>) — one row per household in the tenant-anchor <c>identity</c> schema, so
    /// no separate settings table or RLS wiring is needed. The domain accepts any 3-letter code; the
    /// Settings UI constrains it to a curated 2-minor-unit list. Defaults to USD.
    /// </summary>
    public string DisplayCurrency { get; private set; } = "USD";

    /// <summary>
    /// Household-wide default due-days applied when a lot is frozen and the product carries no
    /// per-product after-freezing override (<c>Product.DefaultDueDaysAfterFreezing</c>), plantry-hh1f.
    /// Catalog's <c>ExpiryDefaultResolver</c> falls back to this via the
    /// <c>IHouseholdExpiryDefaultsReader</c> anti-corruption port so an auto-created product with no
    /// per-product configuration (e.g. leftovers, plantry-hh1f's original report) still gets its expiry
    /// recomputed on freeze instead of the move silently leaving it unchanged. Lives on the aggregate
    /// (like <c>Theme</c>/<c>AiAssistanceEnabled</c>/<c>DisplayCurrency</c>) — one row per household in
    /// the tenant-anchor <c>identity</c> schema, no separate settings table. Defaults to 90 days.
    /// </summary>
    public int DefaultDueDaysAfterFreezing { get; private set; } = 90;

    /// <summary>
    /// Household-wide default due-days applied when a lot is thawed and the product carries no
    /// per-product after-thawing override, mirroring <see cref="DefaultDueDaysAfterFreezing"/>.
    /// Defaults to 3 days.
    /// </summary>
    public int DefaultDueDaysAfterThawing { get; private set; } = 3;

    public DateTimeOffset CreatedAt { get; private set; }

    private Household() { } // EF

    private Household(HouseholdId id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    public static Household Create(string name, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Household(HouseholdId.New(), name.Trim(), clock.UtcNow);
    }

    public void UpdateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void UpdateTheme(string theme)
    {
        if (theme is not ("hearth" or "market" or "slate"))
            throw new ArgumentException($"Unknown theme '{theme}'.", nameof(theme));
        Theme = theme;
    }

    public void SetExpiryWarningDays(int days)
    {
        if (days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        ExpiryWarningDays = days;
    }

    /// <summary>Sets the household's default after-freezing due-days (plantry-hh1f). Non-negative only, mirroring <see cref="SetExpiryWarningDays"/>.</summary>
    public void SetDefaultDueDaysAfterFreezing(int days)
    {
        if (days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        DefaultDueDaysAfterFreezing = days;
    }

    /// <summary>Sets the household's default after-thawing due-days (plantry-hh1f). Non-negative only, mirroring <see cref="SetExpiryWarningDays"/>.</summary>
    public void SetDefaultDueDaysAfterThawing(int days)
    {
        if (days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        DefaultDueDaysAfterThawing = days;
    }

    /// <summary>Enables or disables the household's assistive-AI features (plantry-qll2.1).</summary>
    public void SetAiAssistanceEnabled(bool enabled) => AiAssistanceEnabled = enabled;

    /// <summary>
    /// Sets the household's display currency (plantry-2x6e.1). Trims and upper-cases the input, then
    /// requires exactly three ASCII letters A–Z (the same 3-letter ISO 4217 stance as
    /// <see cref="Money"/>'s constructor); anything else throws <see cref="ArgumentException"/>. The
    /// domain does not restrict to a known-currency list — the Settings UI curates that.
    /// </summary>
    public void SetDisplayCurrency(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(c => c is >= 'A' and <= 'Z'))
            throw new ArgumentException("Display currency must be a 3-letter ISO 4217 code (A–Z).", nameof(code));
        DisplayCurrency = normalized;
    }
}
