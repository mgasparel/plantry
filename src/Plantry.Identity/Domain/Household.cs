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

    /// <summary>Enables or disables the household's assistive-AI features (plantry-qll2.1).</summary>
    public void SetAiAssistanceEnabled(bool enabled) => AiAssistanceEnabled = enabled;
}
