namespace Plantry.Web.Pages.Shared;

/// <summary>
/// View model for the <c>_CatChip</c> partial — a category colour chip that derives
/// its two-letter code from <paramref name="Name"/> and its oklch hue from
/// <paramref name="Hue"/>. When <paramref name="Hue"/> is null the chip renders with
/// the neutral modifier (no colour).
/// </summary>
/// <param name="Name">Category name used to derive the chip code (first 1–2 letters, uppercased).</param>
/// <param name="Hue">oklch hue angle (0–360) for the chip colour; null renders neutral.</param>
public sealed record CatChipViewModel(string? Name, int? Hue);
