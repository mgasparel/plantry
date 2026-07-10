using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E journey (Playwright) for the household invite → join flow (plantry-mfli).
///
/// Acceptance criteria proven here:
///   • Following a valid link + registering lands the new user in the INVITING household — asserted by
///     the second user seeing the first user's data (the first user appears in the shared member roster).
///   • An invalid/unknown token shows a friendly dead-end, not an exception.
///
/// Boots the whole service graph from the Aspire AppHost via AppHostFixture — no manually started app.
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class HouseholdJoinJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    private string BaseUrl => appHost.BaseUrl;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact(DisplayName = "Invite → join: second user registers via the link and lands in the inviting household (sees the first user); invite is consumed; duplicate email fails cleanly")]
    public async Task Invite_Then_Join_Lands_Second_User_In_Inviting_Household()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerEmail = $"owner-{suffix}@test.local";
        var ownerName = $"Owner {suffix}";
        var inviteeEmail = $"invitee-{suffix}@test.local";
        var inviteeName = $"Invitee {suffix}";
        var spareInviteeEmail = $"spare-{suffix}@test.local";
        const string password = "testpass1";

        string joinUrl;
        string spareJoinUrl;

        // ── Household owner: register, then issue two invites and copy their join links ──────────
        await using (var ownerCtx = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true }))
        {
            var page = await ownerCtx.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await page.GotoAsync($"{BaseUrl}/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", $"Join Test Household {suffix}");
            await page.FillAsync("[name='Input.Email']", ownerEmail);
            await page.FillAsync("[name='Input.DisplayName']", ownerName);
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            await page.GotoAsync($"{BaseUrl}/Settings/Members");
            await page.WaitForURLAsync("**/Settings/Members**");

            joinUrl = await IssueInviteAndReadLink(page, inviteeEmail);
            spareJoinUrl = await IssueInviteAndReadLink(page, spareInviteeEmail);
        }

        Assert.False(string.IsNullOrWhiteSpace(joinUrl));
        Assert.NotEqual(joinUrl, spareJoinUrl);

        // ── Invitee: fresh context (no cookies), follow the link and register into the household ──
        await using (var inviteeCtx = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true }))
        {
            var page = await inviteeCtx.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await page.GotoAsync(joinUrl);

            // The invited email is prefilled (editable). Confirm, then complete registration.
            var emailField = page.Locator("[name='Input.Email']");
            await Assertions.Expect(emailField).ToHaveValueAsync(inviteeEmail);

            await page.FillAsync("[name='Input.DisplayName']", inviteeName);
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");

            // Lands in the app, signed in.
            await page.WaitForURLAsync("**/Today**");

            // Proof of shared household: the invitee sees the OWNER in the member roster (the owner's data).
            await page.GotoAsync($"{BaseUrl}/Settings/Members");
            await page.WaitForURLAsync("**/Settings/Members**");

            var names = await page.Locator(".member-row__name").AllTextContentsAsync();
            var trimmed = names.Select(n => n.Trim()).ToList();
            Assert.Contains(ownerName, trimmed);
            Assert.Contains(inviteeName, trimmed);
            Assert.Equal(2, trimmed.Count);
        }

        // ── The used invite is consumed: re-following the SAME link (fresh, unauthenticated) hits the ──
        //    dead-end, not the form. This proves the join marked the invite accepted (ADR-010 step 2).
        await using (var revisitCtx = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true }))
        {
            var page = await revisitCtx.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await page.GotoAsync(joinUrl);

            await Assertions.Expect(page.Locator(".auth-card__heading")).ToContainTextAsync("Invite unavailable");
            await Assertions.Expect(page.Locator("[name='Input.Password']")).ToHaveCountAsync(0);
        }

        // ── Duplicate email fails cleanly: a still-valid spare invite, but registering with an email that ──
        //    already belongs to a user, re-renders the form with an error and creates no second account.
        await using (var dupeCtx = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true }))
        {
            var page = await dupeCtx.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await page.GotoAsync(spareJoinUrl);

            // Override the prefilled email with one that already exists (the owner's).
            await page.FillAsync("[name='Input.Email']", ownerEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Dupe Attempt");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");

            // Stays on the Join form (no redirect to Today) with a clear validation error.
            await Assertions.Expect(page.Locator("[name='Input.Password']")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByText("already exists", new() { Exact = false }).First)
                .ToBeVisibleAsync();
            Assert.DoesNotContain("/Today", page.Url);
        }
    }

    /// <summary>Issues an invite for <paramref name="email"/> on the open Members page and returns its join link.</summary>
    private static async Task<string> IssueInviteAndReadLink(IPage page, string email)
    {
        await page.FillAsync("input[aria-label='Invitee email']", email);
        var inviteBtn = page.Locator(".invite-add__btn");
        await Assertions.Expect(inviteBtn).ToBeEnabledAsync();
        await inviteBtn.ClickAsync();

        // The freshly-issued invite's copy-able link input carries its unique token. Scope to the row
        // whose email cell matches, so issuing a second invite doesn't ambiguate the selector.
        var row = page.Locator(".invite-row").Filter(new() { HasText = email });
        var linkInput = row.Locator(".invite-link__url");
        await Assertions.Expect(linkInput).ToBeVisibleAsync();
        var url = await linkInput.InputValueAsync();
        Assert.Contains("/Account/Join?token=", url);
        return url;
    }

    [Fact(DisplayName = "Join with an unknown token shows a friendly dead-end, not an exception")]
    public async Task Join_With_Unknown_Token_Shows_DeadEnd()
    {
        await using var ctx = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var page = await ctx.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        var response = await page.GotoAsync($"{BaseUrl}/Account/Join?token=not-a-real-token-{Guid.NewGuid():N}");

        // A friendly page (200), not a 5xx exception page.
        Assert.NotNull(response);
        Assert.True(response!.Ok, $"Expected a 2xx dead-end page, got {(int)response.Status}.");

        // The dead-end renders the invite-unavailable heading and no registration form.
        await Assertions.Expect(page.Locator(".auth-card__heading")).ToContainTextAsync("Invite unavailable");
        await Assertions.Expect(page.Locator("[name='Input.Password']")).ToHaveCountAsync(0);
    }
}
