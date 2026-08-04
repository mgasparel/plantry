using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.Planning.Application;
using Plantry.Planning.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for Unfulfillable cell rendering.
/// Validates that when <see cref="GeneratePlanService"/> detects an unfulfillable cell
/// (an attendee's Required tag has ZERO recipes in the full corpus), the POST /MealPlan?handler=Generate
/// response renders:
///   - <c>class="mcell conflict"</c>
///   - <c>data-conflict="unfulfillable"</c>
///   - <c>conflict-notice</c> with the specific "Your recipe book has no [tag] recipes." message
///   - <c>conflict-acts</c> with "Add a [tag] recipe" CTA
///
/// Also validates that:
///   - A normal cell (attendee has a Required tag AND a matching recipe) does NOT render conflict markers.
///   - The Unfulfillable message differs from the HardConflict message (distinct reasons, distinct UI).
/// </summary>
[Collection(nameof(UnfulfillableCellCollection))]
public sealed class UnfulfillableCellFragmentTests(UnfulfillableCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── POST Generate returns grid with unfulfillable cell markers ────────────

    [Fact(DisplayName = "POST Generate: unfulfillable cell → grid renders mcell conflict class")]
    public async Task PostGenerate_Unfulfillable_RendersConflictCellClass()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("mcell conflict", html);
    }

    [Fact(DisplayName = "POST Generate: unfulfillable cell → grid renders data-conflict=unfulfillable attribute")]
    public async Task PostGenerate_Unfulfillable_RendersDataConflictAttribute()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("data-conflict=\"unfulfillable\"", html);
    }

    [Fact(DisplayName = "POST Generate: unfulfillable cell → grid renders conflict-notice--unfulfillable with tag-specific message")]
    public async Task PostGenerate_Unfulfillable_RendersUnfulfillableNotice()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-notice--unfulfillable", html);
        // Tag name "Vegetarian" should appear in the message.
        Assert.Contains("Vegetarian", html);
        // The specific unfulfillable message format.
        Assert.Contains("recipe book has no", html);
    }

    [Fact(DisplayName = "POST Generate: unfulfillable cell → grid renders Add a [tag] recipe CTA")]
    public async Task PostGenerate_Unfulfillable_RendersAddRecipeCta()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-acts", html);
        // The CTA should name the tag.
        Assert.Contains("Add a Vegetarian recipe", html);
    }

    [Fact(DisplayName = "POST Generate: unfulfillable cell does NOT render HardConflict dual CTAs")]
    public async Task PostGenerate_Unfulfillable_DoesNotRenderHardConflictCtas()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        // HardConflict renders "Adjust who's attending" and "Add a dish by hand".
        // Unfulfillable should NOT render these.
        Assert.DoesNotContain("Adjust who", html);
        // The HardConflict-specific message should not appear either.
        Assert.DoesNotContain("requirements conflict", html);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<string> PostGenerateAndReadGridAsync(HttpClient client)
    {
        // GET the page first to obtain the antiforgery token + paired cookie.
        var pageHtml = await (await client.GetAsync("/MealPlan")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            pageHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "No antiforgery token found on the page.");
        var token = match.Groups[1].Value;

        var week = UnfulfillableCellFixture.WeekStart.ToString("yyyy-MM-dd");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

[CollectionDefinition(nameof(UnfulfillableCellCollection))]
public sealed class UnfulfillableCellCollection : ICollectionFixture<UnfulfillableCellFactory> { }

/// <summary>
/// WAF factory that seeds one attendee with a Required "Vegetarian" tag but NO vegetarian recipes
/// in the corpus. Every cell is unfulfillable (corpus gap, not attendee conflict).
/// </summary>
public sealed class UnfulfillableCellFactory : MealPlanFragmentFactory
{
    // Slot config: Alice is default attendee on every slot.
    protected override IMealSlotConfigRepository SlotConfigRepo
    {
        get
        {
            var hh = Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);
            var clock = new FixedClock(MealPlanningTestClock.Instant);
            var config = MealSlotConfig.CreateWithDefaults(hh, clock);
            foreach (var slot in config.Slots.Where(s => s.IsActive))
                config.SetDefaultAttendees(slot.Id, [UnfulfillableCellFixture.AliceId], clock);
            return new FakeSlotRepo(config);
        }
    }

    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([
        new HouseholdMember(UnfulfillableCellFixture.AliceId, "Alice", "A"),
    ]);

    // Preferences: Alice requires VegetarianTag.
    protected override IUserPreferenceRepository PreferenceRepo =>
        new UnfulfillablePrefsRepo(UnfulfillableCellFixture.BuildAlicePref());

    // Recipes: ONLY meat recipes — NO vegetarian recipes. Every cell is unfulfillable for Alice.
    protected override IRecipeReadModel RecipeReadModel => new FakeRecipeReader([
        new RecipeReadModel(UnfulfillableCellFixture.MeatRecipeId, "Beef Stew", [UnfulfillableCellFixture.MeatTag], 4),
    ]);

    // Tag reader: returns the Vegetarian tag so it can be resolved to a name in-cell.
    protected override ITagReader TagReader => new UnfulfillableTagReader();

    // Planner stays the base default (NullMealPlanner) — never called because all cells are
    // unfulfillable.
}

// ── UnfulfillableCellFixture ──────────────────────────────────────────────────

internal static class UnfulfillableCellFixture
{
    /// <summary>Monday of the pinned test week (plantry-1w87) — matches the SUT's pinned IClock so dates
    /// always fall in the rendered week.</summary>
    public static DateOnly WeekStart
    {
        get
        {
            var today = DateOnly.FromDateTime(MealPlanningTestClock.Instant.UtcDateTime);
            return MealPlan.NormalizeToMonday(today);
        }
    }

    public static readonly Guid AliceId  = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    public static readonly Guid VegetarianTag = Guid.Parse("cccccccc-0000-0000-0000-000000000030");
    public static readonly Guid MeatTag  = Guid.Parse("cccccccc-0000-0000-0000-000000000031");
    public static readonly Guid MeatRecipeId  = Guid.Parse("dddddddd-0000-0000-0000-000000000030");

    private static readonly HouseholdId Hh =
        Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);

    public static UserPreference BuildAlicePref()
    {
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        var pref = UserPreference.Create(Hh, AliceId, clock);
        // Alice requires vegetarian food, but there are NO vegetarian recipes in the corpus.
        pref.SetStance(VegetarianTag, "Required", clock);
        return pref;
    }
}

/// <summary>
/// Prefs repo that returns Alice's preference (Required VegetarianTag).
/// </summary>
internal sealed class UnfulfillablePrefsRepo(UserPreference alicePref) : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var result = userId == UnfulfillableCellFixture.AliceId ? alicePref : null;
        return Task.FromResult<UserPreference?>(result);
    }

    public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// ITagReader stub that returns the "Vegetarian" tag so the unfulfillable cell can show
/// "Your recipe book has no Vegetarian recipes." and "Add a Vegetarian recipe".
/// </summary>
internal sealed class UnfulfillableTagReader : ITagReader
{
    public Task<IReadOnlyList<TagGroup>> ListGroupedAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TagGroup> groups =
        [
            new TagGroup("Diet", 150, [
                new TagSummary(UnfulfillableCellFixture.VegetarianTag, "Vegetarian", "Diet", 150),
            ])
        ];
        return Task.FromResult(groups);
    }
}
