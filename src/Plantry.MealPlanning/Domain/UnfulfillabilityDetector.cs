namespace Plantry.MealPlanning.Domain;

/// <summary>
/// Pure domain service that detects unfulfillable cells: cells where at least one attendee's
/// Required tag has ZERO satisfying recipes in the FULL corpus — making it impossible to
/// ever satisfy that attendee from the existing recipe book, regardless of how many candidates
/// the AI could choose from.
///
/// This is fundamentally different from a <see cref="HardStanceConflict"/> (which means
/// multiple attendees each have recipes but no SINGLE recipe satisfies everyone). Unfulfillable
/// means one attendee's dietary requirement has NO recipes in the book at all.
///
/// Crucially, detection uses a TARGETED corpus query (<paramref name="anyRecipeWithTag"/>),
/// never the 50-cap candidate list from SearchAsync. A confident "you have no vegetarian recipes"
/// would be wrong if the book has 60 recipes and vegetarian ones fell outside the top 50.
/// </summary>
public static class UnfulfillabilityDetector
{
    /// <summary>
    /// Detects whether any attendee's Required tag has zero satisfying recipes in the full corpus.
    /// </summary>
    /// <param name="constraints">
    /// Generation constraints carrying per-attendee hard stances (RequiredTagIds, RestrictedTagIds).
    /// </param>
    /// <param name="anyRecipeWithTag">
    /// Targeted corpus query: returns true when ANY recipe in the household's recipe book carries
    /// the given tag. Must query the FULL corpus, not the 50-cap candidate list.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="null"/> when every attendee's Required tags are satisfiable (at least one
    /// recipe exists for each Required tag in the full corpus).
    /// An <see cref="UnfulfillableResult"/> for the FIRST attendee whose Required tag has no
    /// satisfying recipe in the corpus.
    /// </returns>
    public static async Task<UnfulfillableResult?> DetectAsync(
        GenerationConstraints constraints,
        Func<Guid, Task<bool>> anyRecipeWithTag,
        CancellationToken ct = default)
    {
        foreach (var attendee in constraints.AttendeeStances)
        {
            foreach (var tagId in attendee.RequiredTagIds)
            {
                ct.ThrowIfCancellationRequested();
                var hasRecipe = await anyRecipeWithTag(tagId);
                if (!hasRecipe)
                    return new UnfulfillableResult(attendee.UserId, tagId);
            }
        }

        return null;
    }
}

/// <summary>
/// Descriptor for an unfulfillable cell: one attendee has a Required tag (dietary need) for
/// which ZERO recipes exist in the full recipe corpus. No AI generation can fill this cell
/// until the user adds a recipe matching the Required tag.
/// </summary>
/// <param name="AttendeeId">User ID of the attendee whose Required tag has no satisfying recipe.</param>
/// <param name="UnfulfillableTagId">
/// The Required tag ID that has no satisfying recipe in the full corpus.
/// </param>
public sealed record UnfulfillableResult(Guid AttendeeId, Guid UnfulfillableTagId);
