using Microsoft.AspNetCore.Identity;
using Plantry.SharedKernel;

namespace Plantry.Identity.Infrastructure;

public sealed class AppUser : IdentityUser
{
    /// <summary>The household this user belongs to. Set on registration.</summary>
    public Guid HouseholdId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}
