using Plantry.Intake.Domain;

namespace Plantry.Intake.Application;

/// <summary>
/// Shared amount/item-count projection rules for an <see cref="ImportSession"/> (receipt-intake-history.md
/// H6), so <see cref="GetRecentSessionsQuery"/> (the Upload panel) and <see cref="GetIntakeHistoryQuery"/>
/// (the full history page) can never drift on what a row's total/item-count means.
/// </summary>
internal static class IntakeSessionProjection
{
    /// <summary>
    /// H6 amount rule: a <see cref="ImportStatus.Committed"/> session prefers the receipt's own parsed
    /// <see cref="ImportSession.Total"/> (receipt truth), then the sum of its committed lines'
    /// <see cref="ImportLine.Price"/> (user-resolved truth) when the receipt carried no total. A
    /// <see cref="ImportStatus.Ready"/> session (nothing committed yet) falls back to the sum of
    /// AI-<see cref="ImportLine.SuggestedPrice"/> across its lines. Any other status (Failed, Discarded,
    /// Parsing) has nothing priced to show.
    /// </summary>
    public static decimal? Amount(ImportSession session)
    {
        if (session.Status == ImportStatus.Committed)
        {
            if (session.Total is { } total)
                return total;

            var committedPrices = session.Lines
                .Where(l => l.Status == LineStatus.Committed && l.Price.HasValue)
                .Select(l => l.Price!.Value)
                .ToList();
            return committedPrices.Count > 0 ? committedPrices.Sum() : null;
        }

        if (session.Status == ImportStatus.Ready)
        {
            var suggested = session.Lines
                .Where(l => l.SuggestedPrice.HasValue)
                .Select(l => l.SuggestedPrice!.Value)
                .ToList();
            return suggested.Count > 0 ? suggested.Sum() : null;
        }

        return null; // Failed / Discarded / Parsing — nothing priced to show
    }

    /// <summary>
    /// H6 item-count rule: a Committed session counts its committed lines; a Ready session counts every
    /// AI-parsed line (the deck hasn't resolved them all yet, so "committed" has no meaning); any other
    /// status has nothing to count.
    /// </summary>
    public static int? ItemCount(ImportSession session) => session.Status switch
    {
        ImportStatus.Committed => session.Lines.Count(l => l.Status == LineStatus.Committed),
        ImportStatus.Ready => session.Lines.Count,
        _ => null,
    };
}
