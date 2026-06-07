using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Identity.Domain;

public sealed class Household : AggregateRoot<HouseholdId>
{
    public string Name { get; private set; } = string.Empty;
    public string? EmailIntakeAddress { get; private set; }
    public int ExpiryWarningDays { get; private set; } = 3;
    public string Theme { get; private set; } = "slate";
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
}
