using Microsoft.AspNetCore.Mvc.Testing;
using Plantry.MealPlanning.Application;
using Plantry.MealPlanning.Domain;
using Plantry.SharedKernel;
using Plantry.Tests.Web.Infrastructure;

namespace Plantry.Tests.Web.MealPlanning;

/// <summary>
/// L4 fragment tests for C6 hard-stance conflict cell rendering.
/// Validates that when <see cref="GeneratePlanService"/> detects an irreconcilable
/// hard-stance conflict (two attendees whose Required tags exclude every candidate),
/// the POST /MealPlan?handler=Generate response renders the conflict markers:
///   - <c>class="mcell conflict"</c>
///   - <c>data-conflict="hard-stance"</c>
///   - <c>conflict-notice</c> div with text "No single dish suits everyone here"
/// </summary>
[Collection(nameof(ConflictCellCollection))]
public sealed class ConflictCellFragmentTests(ConflictCellFactory factory)
{
    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.HouseholdHeader, WeekGridFixture.HouseholdId.ToString());
        return client;
    }

    // ── POST Generate returns grid with conflict cell markers ─────────────────

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders mcell conflict class")]
    public async Task PostGenerate_HardConflict_RendersConflictCellClass()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("mcell conflict", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders data-conflict attribute")]
    public async Task PostGenerate_HardConflict_RendersDataConflictAttribute()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("data-conflict=\"hard-stance\"", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders conflict-notice with full actionable message")]
    public async Task PostGenerate_HardConflict_RendersConflictNoticeText()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-notice", html);
        // so5.5 supersedes so5.4's minimal seed with the full actionable message + dual CTAs.
        Assert.Contains("requirements conflict", html);
    }

    [Fact(DisplayName = "POST Generate: irreconcilable hard-stance → grid renders dual CTAs (add by hand + adjust attendance)")]
    public async Task PostGenerate_HardConflict_RendersDualCtas()
    {
        var client = CreateClient();
        var html = await PostGenerateAndReadGridAsync(client);

        Assert.Contains("conflict-acts", html);
        Assert.Contains("Add a dish by hand", html);
        Assert.Contains("Adjust who", html); // "Adjust who's attending"
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

        var week = ConflictCellFixture.WeekStart.ToString("yyyy-MM-dd");
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        });

        var response = await client.PostAsync($"/MealPlan?handler=Generate&week={week}", form);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

[CollectionDefinition(nameof(ConflictCellCollection))]
public sealed class ConflictCellCollection : ICollectionFixture<ConflictCellFactory> { }

/// <summary>
/// WAF factory that seeds two attendees with mutually exclusive Required stances and a
/// candidate pool where no recipe satisfies both — every cell is irreconcilable (C6).
/// </summary>
public sealed class ConflictCellFactory : MealPlanFragmentFactory
{
    // Slot config: both Alice and Bob are default attendees on every slot.
    protected override IMealSlotConfigRepository SlotConfigRepo
    {
        get
        {
            var hh = Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);
            var clock = new FixedClock(MealPlanningTestClock.Instant);
            var config = MealSlotConfig.CreateWithDefaults(hh, clock);
            foreach (var slot in config.Slots.Where(s => s.IsActive))
                config.SetDefaultAttendees(slot.Id, [ConflictCellFixture.AliceId, ConflictCellFixture.BobId], clock);
            return new FakeSlotRepo(config);
        }
    }

    protected override IHouseholdMemberReader MemberReader => new FakeMemberReader([
        new HouseholdMember(ConflictCellFixture.AliceId, "Alice", "A"),
        new HouseholdMember(ConflictCellFixture.BobId, "Bob", "B"),
    ]);

    // Preferences: Alice requires VeganTag, Bob requires MeatTag.
    protected override IUserPreferenceRepository PreferenceRepo =>
        new ConflictPrefsRepo(ConflictCellFixture.BuildAlicePref(), ConflictCellFixture.BuildBobPref());

    // Recipes: one vegan (only VeganTag) + one meat (only MeatTag). No recipe covers both.
    protected override IRecipeReadModel RecipeReadModel => new FakeRecipeReader([
        new RecipeReadModel(ConflictCellFixture.VeganRecipeId, "Vegan Stir-Fry", [ConflictCellFixture.VeganTag], 2),
        new RecipeReadModel(ConflictCellFixture.MeatRecipeId, "Beef Stew", [ConflictCellFixture.MeatTag], 4),
    ]);

    // Tag reader stays the base default (NullTagReader): both VeganTag and MeatTag have recipes, so
    // no cell is Unfulfillable — only HardConflict — and UnfulfillabilityDetector never needs a name.
    // Planner stays NullMealPlanner (base default) — never called because all cells conflict.
}

// ── ConflictCellFixture ───────────────────────────────────────────────────────

internal static class ConflictCellFixture
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

    public static readonly Guid AliceId  = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid BobId    = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid VeganTag = Guid.Parse("cccccccc-0000-0000-0000-000000000010");
    public static readonly Guid MeatTag  = Guid.Parse("cccccccc-0000-0000-0000-000000000011");

    public static readonly Guid VeganRecipeId = Guid.Parse("dddddddd-0000-0000-0000-000000000020");
    public static readonly Guid MeatRecipeId  = Guid.Parse("dddddddd-0000-0000-0000-000000000021");

    private static readonly HouseholdId Hh =
        Plantry.SharedKernel.HouseholdId.From(WeekGridFixture.HouseholdId);

    public static UserPreference BuildAlicePref()
    {
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        var pref = UserPreference.Create(Hh, AliceId, clock);
        pref.SetStance(VeganTag, "Required", clock);
        return pref;
    }

    public static UserPreference BuildBobPref()
    {
        var clock = new FixedClock(MealPlanningTestClock.Instant);
        var pref = UserPreference.Create(Hh, BobId, clock);
        pref.SetStance(MeatTag, "Required", clock);
        return pref;
    }
}

/// <summary>
/// Prefs repo that returns Alice's or Bob's preferences by their seeded user IDs.
/// </summary>
internal sealed class ConflictPrefsRepo(UserPreference alicePref, UserPreference bobPref) : IUserPreferenceRepository
{
    public Task<UserPreference?> FindByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == ConflictCellFixture.AliceId) return Task.FromResult<UserPreference?>(alicePref);
        if (userId == ConflictCellFixture.BobId) return Task.FromResult<UserPreference?>(bobPref);
        return Task.FromResult<UserPreference?>(null);
    }

    public Task AddAsync(UserPreference preference, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// NullTagReader moved to Infrastructure/WeekGridFixture.cs (plantry-ej84) — it's a shared default
// MealPlanFragmentFactory reaches back into, so it now lives alongside the factory instead of being
// reached for across a feature-namespace `using`.
