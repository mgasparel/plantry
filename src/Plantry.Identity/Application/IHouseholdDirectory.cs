namespace Plantry.Identity.Application;

/// <summary>
/// Identity-owned read port (plantry-m1u): the members of the caller's current household as plain
/// identity facts — user id + display name — ordered by display name, empty when there is no tenant
/// household. Lets a cross-context adapter obtain household-member display data WITHOUT depending on
/// ASP.NET Identity / <c>UserManager</c>; the ASP.NET-coupled implementation lives in
/// Plantry.Identity.Infrastructure, keeping the composition assembly free of Microsoft.AspNetCore.*.
/// </summary>
public interface IHouseholdDirectory
{
    Task<IReadOnlyList<HouseholdUser>> ListMembersAsync(CancellationToken ct = default);
}

/// <summary>
/// A single member of the current household, as raw Identity facts. <see cref="UserId"/> is the raw
/// Identity user id string (the caller performs any Guid parsing / presentation mapping it needs).
/// </summary>
public sealed record HouseholdUser(string UserId, string DisplayName);
