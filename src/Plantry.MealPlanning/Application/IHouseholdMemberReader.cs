namespace Plantry.MealPlanning.Application;

/// <summary>
/// Port: read the members of the current household for the attendee picker.
/// Implemented in Plantry.Web (adapter over ASP.NET Identity).
/// </summary>
public interface IHouseholdMemberReader
{
    Task<IReadOnlyList<HouseholdMember>> GetMembersAsync(Guid householdId, CancellationToken ct = default);
}

public sealed record HouseholdMember(Guid UserId, string DisplayName);
