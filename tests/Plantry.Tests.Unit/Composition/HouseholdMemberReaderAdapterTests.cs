using Plantry.Identity.Application;
using Plantry.Web.MealPlanning;

namespace Plantry.Tests.Unit.Composition;

/// <summary>
/// L2 tests for <see cref="HouseholdMemberReaderAdapter"/> (plantry-riqy, plantry-m1u) — the
/// MealPlanning→Identity ACL adapter. Covers the Guid parse + Initials presentation mapping onto
/// MealPlanning's <see cref="Plantry.MealPlanning.Application.HouseholdMember"/> contract: two-word
/// names, single-word names, and the blank-name degrade case.
/// </summary>
public sealed class HouseholdMemberReaderAdapterTests
{
    [Fact(DisplayName = "ListMembersAsync maps UserId, DisplayName, and two-letter Initials for a multi-word name")]
    public async Task Maps_TwoWord_Name_To_TwoLetter_Initials()
    {
        var userId = Guid.CreateVersion7();
        var directory = new FakeHouseholdDirectory([new HouseholdUser(userId.ToString(), "Jane Doe")]);

        var members = await new HouseholdMemberReaderAdapter(directory).ListMembersAsync();

        var member = Assert.Single(members);
        Assert.Equal(userId, member.UserId);
        Assert.Equal("Jane Doe", member.DisplayName);
        Assert.Equal("JD", member.Initials);
    }

    [Fact(DisplayName = "ListMembersAsync maps a single-word name to its single upper-cased initial")]
    public async Task Maps_SingleWord_Name_To_One_Initial()
    {
        var directory = new FakeHouseholdDirectory([new HouseholdUser(Guid.CreateVersion7().ToString(), "cher")]);

        var member = Assert.Single(await new HouseholdMemberReaderAdapter(directory).ListMembersAsync());

        Assert.Equal("C", member.Initials);
    }

    [Fact(DisplayName = "ListMembersAsync trims a padded single-word name before taking its initial")]
    public async Task Maps_Padded_SingleWord_Name_To_One_Initial()
    {
        // Display names are stored verbatim (Register.cshtml.cs does not trim), so the single-word
        // branch must read the trimmed parts, not the raw string — otherwise the initial is a space.
        var directory = new FakeHouseholdDirectory([new HouseholdUser(Guid.CreateVersion7().ToString(), "  cher")]);

        var member = Assert.Single(await new HouseholdMemberReaderAdapter(directory).ListMembersAsync());

        Assert.Equal("C", member.Initials);
    }

    [Fact(DisplayName = "ListMembersAsync degrades a blank display name to \"?\" initials")]
    public async Task Maps_Blank_Name_To_QuestionMark()
    {
        var directory = new FakeHouseholdDirectory([new HouseholdUser(Guid.CreateVersion7().ToString(), "   ")]);

        var member = Assert.Single(await new HouseholdMemberReaderAdapter(directory).ListMembersAsync());

        Assert.Equal("?", member.Initials);
    }

    [Fact(DisplayName = "ListMembersAsync maps a three-plus-word name using only the first and last initials")]
    public async Task Maps_MultiWord_Name_Using_First_And_Last()
    {
        var directory = new FakeHouseholdDirectory([new HouseholdUser(Guid.CreateVersion7().ToString(), "Mary Jane Watson")]);

        var member = Assert.Single(await new HouseholdMemberReaderAdapter(directory).ListMembersAsync());

        Assert.Equal("MW", member.Initials);
    }

    private sealed class FakeHouseholdDirectory(IReadOnlyList<HouseholdUser> members) : IHouseholdDirectory
    {
        public Task<IReadOnlyList<HouseholdUser>> ListMembersAsync(CancellationToken ct = default) =>
            Task.FromResult(members);
    }
}
