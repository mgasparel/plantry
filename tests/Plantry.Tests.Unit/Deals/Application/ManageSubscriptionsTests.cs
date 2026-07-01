using Plantry.Deals.Application;
using Plantry.Deals.Domain;
using Xunit;

namespace Plantry.Tests.Unit.Deals.Application;

/// <summary>
/// L2 tests for <see cref="ManageSubscriptions"/> (DJ1): subscribe ensures the store then creates the
/// subscription (unique per household+store); re-subscribe to a removed/paused store reactivates the
/// same row (idempotent, memory retained); pause/resume/unsubscribe toggle IsActive without data loss;
/// the directory search is stubbed; the §7e read model resolves store names + last-pull status.
/// </summary>
public sealed class ManageSubscriptionsTests
{
    private static readonly Guid Household = Guid.NewGuid();

    private static ManageSubscriptions Build(
        out FakeStoreSubscriptionRepository subs,
        out FakeCatalogStoreWriter writer,
        out FakeCatalogStoreReader reader,
        out FakeFlyerSource flyer,
        Guid? household = null,
        TestClock? clock = null)
    {
        subs = new FakeStoreSubscriptionRepository();
        writer = new FakeCatalogStoreWriter();
        reader = new FakeCatalogStoreReader();
        flyer = new FakeFlyerSource();
        var tenant = new FakeTenantContext(household ?? Household);
        return new ManageSubscriptions(subs, reader, writer, flyer, tenant, clock ?? new TestClock());
    }

    [Fact(DisplayName = "Subscribe ensures the catalog.store then creates the StoreSubscription")]
    public async Task Subscribe_Ensures_Store_Then_Creates_Subscription()
    {
        var service = Build(out var subs, out var writer, out _, out _);

        var result = await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1");

        Assert.True(result.IsSuccess);
        var ensured = Assert.Single(writer.EnsureCalls);
        Assert.Equal("flipp-freshco", ensured.ExternalRef);
        var sub = Assert.Single(subs.Items);
        Assert.Equal(result.Value, sub.Id);
        Assert.True(sub.IsActive);
        Assert.Equal("K1A0B1", sub.PostalCode);
        Assert.Equal(Household, sub.HouseholdId.Value);
        Assert.Null(sub.LastPulledAt); // display-only until P5-6
    }

    [Fact(DisplayName = "Subscribe fails Unauthorized when no household is in context")]
    public async Task Subscribe_Fails_Without_Household()
    {
        var subs = new FakeStoreSubscriptionRepository();
        var service = new ManageSubscriptions(
            subs,
            new FakeCatalogStoreReader(),
            new FakeCatalogStoreWriter(),
            new FakeFlyerSource(),
            new FakeTenantContext(null),
            new TestClock());

        var result = await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1");

        Assert.True(result.IsFailure);
        Assert.Equal("Unauthorized", result.Error.Code);
        Assert.Empty(subs.Items);
    }

    [Fact(DisplayName = "Subscribe fails when the postal code is blank")]
    public async Task Subscribe_Fails_On_Blank_Postal_Code()
    {
        var service = Build(out var subs, out _, out _, out _);

        var result = await service.SubscribeAsync("flipp-freshco", "FreshCo", "   ");

        Assert.True(result.IsFailure);
        Assert.Equal("Deals.BlankPostalCode", result.Error.Code);
        Assert.Empty(subs.Items);
    }

    [Fact(DisplayName = "Re-subscribe to the same store reactivates the existing row (idempotent, no duplicate)")]
    public async Task Resubscribe_Reactivates_Same_Row()
    {
        var service = Build(out var subs, out _, out _, out _);

        var first = await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1");
        // Unsubscribe (soft-deactivate), then re-subscribe.
        await service.UnsubscribeAsync(first.Value);
        Assert.False(Assert.Single(subs.Items).IsActive);

        var second = await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1");

        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);   // same subscription id — row reused
        var sub = Assert.Single(subs.Items);        // no duplicate inserted (UNIQUE household+store)
        Assert.True(sub.IsActive);                  // reactivated
    }

    [Fact(DisplayName = "Re-subscribe to a paused store resumes it")]
    public async Task Resubscribe_Resumes_Paused_Store()
    {
        var service = Build(out var subs, out _, out _, out _);

        var first = await service.SubscribeAsync("flipp-metro", "Metro", "K1A0B1");
        await service.PauseAsync(first.Value);
        Assert.False(Assert.Single(subs.Items).IsActive);

        var second = await service.SubscribeAsync("flipp-metro", "Metro", "K1A0B1");

        Assert.Equal(first.Value, second.Value);
        Assert.True(Assert.Single(subs.Items).IsActive);
    }

    [Fact(DisplayName = "Pause / Resume / Unsubscribe toggle IsActive without losing the row")]
    public async Task Pause_Resume_Unsubscribe_Toggle_IsActive()
    {
        var service = Build(out var subs, out _, out _, out _);
        var id = (await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1")).Value;

        Assert.True((await service.PauseAsync(id)).IsSuccess);
        Assert.False(Assert.Single(subs.Items).IsActive);

        Assert.True((await service.ResumeAsync(id)).IsSuccess);
        Assert.True(Assert.Single(subs.Items).IsActive);

        Assert.True((await service.UnsubscribeAsync(id)).IsSuccess);
        Assert.False(Assert.Single(subs.Items).IsActive);
        // Row (and its postal code + store ref) is retained — no data loss.
        Assert.Equal("K1A0B1", subs.Items[0].PostalCode);
    }

    [Fact(DisplayName = "Pause / Resume / Unsubscribe return NotFound for an unknown subscription")]
    public async Task Mutations_Return_NotFound_For_Unknown_Id()
    {
        var service = Build(out _, out _, out _, out _);
        var missing = StoreSubscriptionId.New();

        Assert.Equal("NotFound", (await service.PauseAsync(missing)).Error.Code);
        Assert.Equal("NotFound", (await service.ResumeAsync(missing)).Error.Code);
        Assert.Equal("NotFound", (await service.UnsubscribeAsync(missing)).Error.Code);
    }

    [Fact(DisplayName = "SearchDirectory returns the stub merchants; a blank postal code returns none")]
    public async Task SearchDirectory_Stub_And_Blank_Postal()
    {
        var service = Build(out _, out _, out _, out var flyer);

        var hits = await service.SearchDirectoryAsync("K1A0B1", null);
        Assert.Equal(flyer.Merchants.Count, hits.Count);

        var filtered = await service.SearchDirectoryAsync("K1A0B1", "metro");
        Assert.Equal("Metro", Assert.Single(filtered).Name);

        var none = await service.SearchDirectoryAsync("  ", null);
        Assert.Empty(none);
    }

    [Fact(DisplayName = "ListAsync resolves store names and the not-pulled-yet state")]
    public async Task List_Resolves_Names_And_LastPull()
    {
        var service = Build(out var subs, out var writer, out var reader, out _);
        var id = (await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1")).Value;
        // Wire the reader to resolve the ensured store's name.
        var storeId = subs.Items.Single().StoreId;
        reader.Names[storeId] = "FreshCo";

        var views = await service.ListAsync();

        var view = Assert.Single(views);
        Assert.Equal(id, view.Id);
        Assert.Equal("FreshCo", view.StoreName);
        Assert.Equal("K1A0B1", view.PostalCode);
        Assert.True(view.IsActive);
        Assert.Null(view.LastPulledAt);
    }

    [Fact(DisplayName = "ListAsync falls back to a placeholder name when the store cannot be resolved")]
    public async Task List_Unknown_Store_Falls_Back()
    {
        var service = Build(out _, out _, out _, out _);
        await service.SubscribeAsync("flipp-freshco", "FreshCo", "K1A0B1");
        // reader.Names left empty → unresolved.

        var view = Assert.Single(await service.ListAsync());
        Assert.Equal("(unknown store)", view.StoreName);
    }
}
