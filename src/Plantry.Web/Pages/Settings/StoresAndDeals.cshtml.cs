using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Plantry.Deals.Application;
using Plantry.Deals.Domain;

namespace Plantry.Web.Pages.Settings;

/// <summary>
/// §7e Stores &amp; Deals (DJ1). Manage which merchants the household pulls flyer deals from: the
/// subscription list with last-pull status, postal-code store search (stubbed directory), and
/// subscribe/pause/resume/unsubscribe. Follows the <c>Settings/MealSlots</c> htmx-fragment pattern —
/// the page hosts two regions, and each handler returns the partial for the region it mutates.
/// </summary>
[Authorize]
public sealed class StoresAndDealsModel(ManageSubscriptions service) : PageModel
{
    public IReadOnlyList<SubscriptionView> Subscriptions { get; private set; } = [];

    /// <summary>Directory-search results for the current postal code (empty until a search runs).</summary>
    public IReadOnlyList<DirectoryMerchant> SearchResults { get; private set; } = [];

    /// <summary>The postal code the search ran for — carried onto each result's subscribe form.</summary>
    public string PostalCode { get; private set; } = string.Empty;

    /// <summary>The optional store-name filter for the current search.</summary>
    public string? SearchQuery { get; private set; }

    /// <summary>True once a search has been executed (drives the empty vs "no matches" state).</summary>
    public bool Searched { get; private set; }

    public IReadOnlyList<SubscriptionView> ActiveSubscriptions =>
        Subscriptions.Where(s => s.IsActive).ToList();

    public IReadOnlyList<SubscriptionView> PausedSubscriptions =>
        Subscriptions.Where(s => !s.IsActive).ToList();

    public async Task OnGetAsync(CancellationToken ct = default) => await LoadSubscriptionsAsync(ct);

    // htmx fragment — the subscription list only.
    public async Task<IActionResult> OnGetListAsync(CancellationToken ct = default)
    {
        await LoadSubscriptionsAsync(ct);
        return Partial("_StoresList", this);
    }

    // htmx fragment — the directory-search results for a postal code.
    public async Task<IActionResult> OnGetSearchAsync(
        [FromQuery] string? postalCode,
        [FromQuery] string? q,
        CancellationToken ct = default)
    {
        PostalCode = (postalCode ?? string.Empty).Trim();
        SearchQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        Searched = true;
        SearchResults = await service.SearchDirectoryAsync(PostalCode, SearchQuery, ct);
        return Partial("_StoreSearchResults", this);
    }

    public async Task<IActionResult> OnPostSubscribeAsync(
        [FromForm] string externalRef,
        [FromForm] string name,
        [FromForm] string postalCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalRef) || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(postalCode))
            return BadRequest();

        var result = await service.SubscribeAsync(externalRef.Trim(), name.Trim(), postalCode.Trim(), ct);
        if (result.IsFailure)
            ModelState.AddModelError(string.Empty, result.Error.Description);

        await LoadSubscriptionsAsync(ct);
        return Partial("_StoresList", this);
    }

    public async Task<IActionResult> OnPostPauseAsync([FromQuery] Guid id, CancellationToken ct = default)
    {
        await service.PauseAsync(StoreSubscriptionId.From(id), ct);
        await LoadSubscriptionsAsync(ct);
        return Partial("_StoresList", this);
    }

    public async Task<IActionResult> OnPostResumeAsync([FromQuery] Guid id, CancellationToken ct = default)
    {
        await service.ResumeAsync(StoreSubscriptionId.From(id), ct);
        await LoadSubscriptionsAsync(ct);
        return Partial("_StoresList", this);
    }

    public async Task<IActionResult> OnPostUnsubscribeAsync([FromQuery] Guid id, CancellationToken ct = default)
    {
        await service.UnsubscribeAsync(StoreSubscriptionId.From(id), ct);
        await LoadSubscriptionsAsync(ct);
        return Partial("_StoresList", this);
    }

    private async Task LoadSubscriptionsAsync(CancellationToken ct) =>
        Subscriptions = await service.ListAsync(ct);
}
