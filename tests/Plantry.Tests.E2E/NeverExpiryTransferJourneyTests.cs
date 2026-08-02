using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Plantry.Tests.E2E.Infrastructure;
using Xunit;

namespace Plantry.Tests.E2E;

/// <summary>
/// L5 E2E proof for the product-level Never-after-freezing rule: an otherwise expiring lot is
/// moved into a freezer, the preview says "No expiry", the persisted lot has no expiry, and the
/// Today expiring-soon surface no longer lists it.
/// </summary>
[Trait("Category", "E2E")]
[Collection(nameof(AppHostCollection))]
public sealed class NeverExpiryTransferJourneyTests(AppHostFixture appHost) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

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

    [Fact(DisplayName = "Never after freezing: an expiring lot moves to the freezer without expiring-soon UI")]
    public async Task NeverAfterFreezing_ClearsExpiryAndRemovesExpiringSoonItem()
    {
        var uniqueEmail = $"smoke-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Smoke Never Freezes {Guid.NewGuid():N}"[..28];
        var soonExpiry = AppHostFixture.FixedUtcNow.UtcDateTime.Date.AddDays(2).ToString("yyyy-MM-dd");

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

            await page.GotoAsync($"{appHost.BaseUrl}/Account/Register");
            await page.WaitForURLAsync("**/Account/Register");
            await page.FillAsync("[name='Input.HouseholdName']", "Smoke Never Expiry Household");
            await page.FillAsync("[name='Input.Email']", uniqueEmail);
            await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
            await page.FillAsync("[name='Input.Password']", password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync("**/Today**");

            await page.GotoAsync($"{appHost.BaseUrl}/Catalog/Products/Create");
            await page.WaitForURLAsync("**/Catalog/Products/Create");
            await page.FillAsync("[name='Input.Name']", productName);
            await page.SelectOptionAsync("[name='Input.DefaultUnitId']",
                new SelectOptionValue { Label = "g — gram" });
            await page.ClickAsync("button:has-text('Create Product')");
            await page.WaitForURLAsync("**/Catalog/Products/**");
            var productId = new Uri(page.Url).Segments[^1];

            // Configure only the freeze transition. Thaw remains the normal household default.
            await page.Locator("[name='Input.AfterFreezingMode'][value='Never']")
                .CheckAsync(new() { Force = true });
            await page.ClickAsync("button:has-text('Save changes')");
            await page.WaitForURLAsync($"**/Catalog/Products/{productId}");

            // Add a lot that would otherwise appear in the seven-day expiring-soon rail.
            await page.GotoAsync($"{appHost.BaseUrl}/Pantry/Products/Detail/{productId}");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add stock" }).First.ClickAsync();
            var addSheet = page.GetByRole(AriaRole.Dialog, new() { Name = "Add stock" });
            await Assertions.Expect(addSheet).ToBeVisibleAsync();
            await addSheet.Locator("[name='AddStockInput.Quantity']").FillAsync("500");
            await addSheet.Locator("[name='AddStockInput.UnitId']").SelectOptionAsync(
                new SelectOptionValue { Label = "g — gram" });
            await addSheet.Locator("[name='AddStockInput.LocationId']").SelectOptionAsync(
                new SelectOptionValue { Label = "Fridge" });
            await addSheet.Locator("[name='AddStockInput.ExpiryDate']").FillAsync(soonExpiry);
            await addSheet.GetByRole(AriaRole.Button, new() { Name = "Add to pantry" }).ClickAsync();
            await Assertions.Expect(page.Locator("#lots-grid")).ToContainTextAsync("500 g");

            // Prove the lot is genuinely in the expiring-soon projection before the transition,
            // using the same fixed instant that the Testing AppHost injected into IClock.
            await page.GotoAsync($"{appHost.BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            var beforeFreezeWidget = page.Locator(".today-exp-widget");
            await Assertions.Expect(beforeFreezeWidget).ToBeVisibleAsync();
            await Assertions.Expect(beforeFreezeWidget.Locator(".today-exp-row", new() { HasText = productName }))
                .ToBeVisibleAsync();

            await page.GotoAsync($"{appHost.BaseUrl}/Pantry/Products/Detail/{productId}");
            await Assertions.Expect(page.Locator("#lots-grid")).ToContainTextAsync("500 g");

            // Open the server-precomputed transition preview before submitting the move.
            await page.Locator("#lots-grid tbody tr").First
                .GetByRole(AriaRole.Button, new() { Name = "Move" }).ClickAsync();
            var moveSheet = page.GetByRole(AriaRole.Dialog, new() { Name = "Move stock" });
            await Assertions.Expect(moveSheet).ToBeVisibleAsync();
            await Assertions.Expect(moveSheet.GetByText("No expiry", new() { Exact = true })).ToBeVisibleAsync();
            await moveSheet.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Freeze") }).ClickAsync();

            // The OOB stock refresh shows the moved lot's null expiry as the neutral dash.
            await Assertions.Expect(page.Locator("#lots-grid")).ToContainTextAsync("—");

            // Null expiry is excluded from the Today expiring-soon projection, even though this
            // exact lot was two days from expiry before the freeze.
            await page.GotoAsync($"{appHost.BaseUrl}/Today");
            await page.WaitForURLAsync("**/Today**");
            var widget = page.Locator(".today-exp-widget");
            await Assertions.Expect(widget).ToBeVisibleAsync();
            await Assertions.Expect(widget).ToContainTextAsync("Nothing expiring this week");
            await Assertions.Expect(widget.Locator(".today-exp-row", new() { HasText = productName }))
                .Not.ToBeVisibleAsync();
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = "trace-never-expiry-transfer.zip" });
        }
    }

    [Fact(DisplayName = "Same-storage move: a Days thaw policy shows only an unchanged expiry preview")]
    public Task SameStorageMove_DaysPolicy_ShowsOnlyUnchangedPreview() =>
        AssertSameStorageMovePreviewAsync("SetDays", "14");

    [Fact(DisplayName = "Same-storage move: a Never thaw policy shows only an unchanged expiry preview")]
    public Task SameStorageMove_NeverPolicy_ShowsOnlyUnchangedPreview() =>
        AssertSameStorageMovePreviewAsync("Never", null);

    private async Task AssertSameStorageMovePreviewAsync(string thawingMode, string? thawingDays)
    {
        var uniqueEmail = $"smoke-same-storage-{Guid.NewGuid():N}@test.local";
        const string password = "testpass1";
        var productName = $"Smoke Same Storage {thawingMode} {Guid.NewGuid():N}"[..28];

        await using var context = await _browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)TimeSpan.FromMinutes(2).TotalMilliseconds);

        await page.GotoAsync($"{appHost.BaseUrl}/Account/Register");
        await page.WaitForURLAsync("**/Account/Register");
        await page.FillAsync("[name='Input.HouseholdName']", "Smoke Same Storage Household");
        await page.FillAsync("[name='Input.Email']", uniqueEmail);
        await page.FillAsync("[name='Input.DisplayName']", "Smoke User");
        await page.FillAsync("[name='Input.Password']", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync("**/Today**");

        await page.GotoAsync($"{appHost.BaseUrl}/Catalog/Products/Create");
        await page.WaitForURLAsync("**/Catalog/Products/Create");
        await page.FillAsync("[name='Input.Name']", productName);
        await page.SelectOptionAsync("[name='Input.DefaultUnitId']",
            new SelectOptionValue { Label = "g — gram" });
        await page.ClickAsync("button:has-text('Create Product')");
        await page.WaitForURLAsync("**/Catalog/Products/**");
        var productId = new Uri(page.Url).Segments[^1];

        await page.Locator($"[name='Input.AfterThawingMode'][value='{thawingMode}']")
            .CheckAsync(new() { Force = true });
        if (thawingDays is not null)
            await page.Locator("[name='Input.DefaultDueDaysAfterThawing']").FillAsync(thawingDays);
        await page.ClickAsync("button:has-text('Save changes')");
        await page.WaitForURLAsync($"**/Catalog/Products/{productId}");

        await page.GotoAsync($"{appHost.BaseUrl}/Pantry/Products/Detail/{productId}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add stock" }).First.ClickAsync();
        var addSheet = page.GetByRole(AriaRole.Dialog, new() { Name = "Add stock" });
        await Assertions.Expect(addSheet).ToBeVisibleAsync();
        await addSheet.Locator("[name='AddStockInput.Quantity']").FillAsync("500");
        await addSheet.Locator("[name='AddStockInput.UnitId']").SelectOptionAsync(
            new SelectOptionValue { Label = "g — gram" });
        await addSheet.Locator("[name='AddStockInput.LocationId']").SelectOptionAsync(
            new SelectOptionValue { Label = "Fridge" });
        await addSheet.Locator("[name='AddStockInput.ExpiryDate']")
            .FillAsync(AppHostFixture.FixedUtcNow.UtcDateTime.Date.AddDays(2).ToString("yyyy-MM-dd"));
        await addSheet.GetByRole(AriaRole.Button, new() { Name = "Add to pantry" }).ClickAsync();
        await Assertions.Expect(page.Locator("#lots-grid")).ToContainTextAsync("500 g");

        await page.Locator("#lots-grid tbody tr").First
            .GetByRole(AriaRole.Button, new() { Name = "Move" }).ClickAsync();
        var moveSheet = page.GetByRole(AriaRole.Dialog, new() { Name = "Move stock" });
        await Assertions.Expect(moveSheet).ToBeVisibleAsync();
        await moveSheet.Locator("select[name='MoveInput.LocationId']").SelectOptionAsync(
            new SelectOptionValue { Label = "Pantry" });

        await Assertions.Expect(moveSheet.GetByText("(unchanged)", new() { Exact = true }))
            .ToBeVisibleAsync();
        Assert.Equal(0, await moveSheet.Locator(".move-preview__v s").CountAsync());
        Assert.Equal(0, await moveSheet.GetByText("No expiry", new() { Exact = true }).CountAsync());
    }
}
