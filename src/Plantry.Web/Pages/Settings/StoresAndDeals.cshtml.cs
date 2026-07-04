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
        // Load subscriptions so already-subscribed merchants render the "Subscribed" badge instead of a
        // (stale) Subscribe button — keeps a re-search consistent with the point-of-action flip (plantry-b9wd).
        await LoadSubscriptionsAsync(ct);
        return Partial("_StoreSearchResults", this);
    }

    public async Task<IActionResult> OnPostSubscribeAsync(
        [FromForm] string externalRef,
        [FromForm] string name,
        [FromForm] string? postalCode,
        CancellationToken ct = default)
    {
        // A missing merchant identity is a malformed request (the form embeds these) — 400.
        // A blank/rejected postal code is a domain failure the service reports, rendered inline below.
        if (string.IsNullOrWhiteSpace(externalRef) || string.IsNullOrWhiteSpace(name))
            return BadRequest();

        var ref_ = externalRef.Trim();
        var storeName = name.Trim();
        var postal = (postalCode ?? string.Empty).Trim();

        var result = await service.SubscribeAsync(ref_, storeName, postal, ct);
        if (result.IsFailure)
            // Re-render THIS result with the Subscribe button retained + an inline error, at 200 so
            // htmx swaps it in (htmx ignores non-2xx bodies by default). DJ1/plantry-b9wd criterion 4.
            return Partial("_StoreSearchResultItem",
                new StoreResultItem(ref_, storeName, postal, IsSubscribed: false, Error: result.Error.Description));

        // Success: primary swap flips the clicked result to the "Subscribed" badge; the #stores-list-card
        // fragment rides along as an out-of-band swap so the subscription list stays in sync (criteria 1–2).
        await LoadSubscriptionsAsync(ct);
        ViewData["ListOob"] = true;
        return Partial("_SubscribeResult",
            new SubscribeResult(new StoreResultItem(ref_, storeName, postal, IsSubscribed: true), this));
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

/// <summary>
/// A single directory search-result row (<c>_StoreSearchResultItem</c>). <see cref="IsSubscribed"/>
/// drives the "Subscribed" badge vs the Subscribe form; <see cref="Error"/>, when set, renders an
/// inline error at the result while retaining the Subscribe button for retry (DJ1/plantry-b9wd).
/// </summary>
public sealed record StoreResultItem(
    string ExternalRef,
    string Name,
    string PostalCode,
    bool IsSubscribed,
    string? Error = null);

/// <summary>
/// The Subscribe-success response (<c>_SubscribeResult</c>): the flipped result item (main swap) plus
/// the subscription list (<see cref="Page"/>) re-rendered as an out-of-band swap into #subs-region.
/// </summary>
public sealed record SubscribeResult(StoreResultItem Item, StoresAndDealsModel Page);
