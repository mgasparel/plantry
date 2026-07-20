using System.Text.Json;
using Plantry.Deals.Application;

namespace Plantry.Web.Pages.Deals;

/// <summary>
/// One card in the step-2 judgement-call deck (bead plantry-q9zr.8), projected for the
/// <c>deal-deck.js</c> island. Presentation-only: it carries the verbatim raw flyer name (ACL
/// quarantine, DD6) alongside the server title-cased display name (q9zr.10), the brand/price, the
/// single suggestion, a noise flag, and the two verb URLs. Deliberately NO store/dates/links/
/// confidence-pill — that ruling is final; per the guided-flow dedupe, flyer context lives in the
/// rail, not on a card. The confidence tier itself is NOT sent: the server already classified this
/// deal into step 2 (<see cref="ReviewStepClassifier"/>); the island only presents it.
/// </summary>
/// <remarks>
/// The wire shape (camelCase) is kept in sync with the <c>DealDeckCard</c> JSDoc <c>@typedef</c> in
/// <c>deal-deck.js</c> by <c>IslandTypedefEquivalenceTests</c> (ADR-020 §6).
/// </remarks>
public sealed record DealDeckCardVm(
    string DealId,
    string RawName,
    string DisplayName,
    string? Brand,
    string Price,
    bool HasSuggestion,
    string? SuggestedProductName,
    string? Reasoning,
    bool IsNoise,
    string ConfirmUrl,
    string RejectUrl);

/// <summary>
/// The hydration payload for the judgement-call deck island: the step-2 deals of the active flyer in
/// server order, plus the flyer key and step the deck's presentation state (order/skip/baseline) is
/// scoped to. Verbs still post through the existing htmx endpoints; this payload is read-only truth the
/// island renders — it never forks server state (ADR-020 §2 / §7).
/// </summary>
public sealed record DealDeckHydration(
    string FlyerKey,
    int Step,
    IReadOnlyList<DealDeckCardVm> Deals)
{
    /// <summary>camelCase, matching the CamelCase serializer policy the other island payloads use.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Serialise for the <c>&lt;script type="application/json"&gt;</c> hydration blob.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Build the deck payload from the active flyer's step-2 deals. The verb URLs mirror the
    /// server-rendered card exactly (<c>/Deals/Review?handler=Confirm&amp;dealId=&amp;flyer=&amp;step=2</c>),
    /// so the deck posts through the same Confirm/Reject handlers and the same re-render path.
    /// </summary>
    public static DealDeckHydration Build(
        IReadOnlyList<DealReviewView> deals, string? flyerKey, string displayCurrency)
    {
        var flyerQs = string.IsNullOrEmpty(flyerKey) ? string.Empty : $"&flyer={flyerKey}";

        var cards = deals.Select(d =>
        {
            var id = d.DealId.Value.ToString();
            return new DealDeckCardVm(
                DealId: id,
                RawName: d.RawName,
                DisplayName: DealReviewDisplay.TitleCase(d.RawName),
                Brand: string.IsNullOrWhiteSpace(d.Brand) ? null : d.Brand,
                Price: MoneyDisplay.Format(d.Price, displayCurrency),
                HasSuggestion: d.HasSuggestion,
                SuggestedProductName: d.SuggestedProductName,
                Reasoning: string.IsNullOrWhiteSpace(d.Reasoning) ? null : d.Reasoning,
                IsNoise: DealReviewDisplay.IsNoise(d.Price),
                ConfirmUrl: $"/Deals/Review?handler=Confirm&dealId={id}{flyerQs}&step=2",
                RejectUrl: $"/Deals/Review?handler=Reject&dealId={id}{flyerQs}&step=2");
        }).ToList();

        return new DealDeckHydration(flyerKey ?? string.Empty, 2, cards);
    }
}
