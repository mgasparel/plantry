using System.Globalization;
using Plantry.Intake.Application;
using Plantry.Intake.Domain;

namespace Plantry.Web.Pages.Intake;

/// <summary>
/// The seven page-handler URLs the island posts row actions to. <c>Url.Page(...)</c> needs
/// <c>PageModel</c> context, so the page computes these and hands them in — keeping
/// <see cref="IntakeReviewHydrationBuilder.Build"/> pure over its inputs (no <c>HttpContext</c>,
/// no <c>Url</c> helper) and therefore directly unit-testable.
/// </summary>
public sealed record ReviewHandlerUrls(
    string Commit,
    string Discard,
    string SaveLine,
    string DismissLine,
    string RestoreLine,
    string Reopen,
    string ConfirmLines,
    string CorrectHeader);

/// <summary>
/// Builds the Intake review island's hydration payload (<see cref="SessionHydration"/>) from a loaded
/// <see cref="SessionReviewView"/> and its reference data — the presentation composition lifted out of
/// <c>ReviewModel</c> (plantry-uk4u) so the page model stays a set of thin handlers and the projection is
/// directly unit-testable. It is <b>pure</b> over its inputs: no <c>HttpContext</c>, no <c>Url</c> helper,
/// no clock read — the page reads the clock and computes the handler URLs, then passes them in. It holds no
/// HTTP concern and executes no command, so it stays in the Web project and does NOT belong in
/// <c>Plantry.Intake</c> (bounded-context discipline, ADR-010).
///
/// <para>The priority chain stays server-side via <see cref="ReviewPrefill.ComputePrefill"/> (ADR-020 §3 /
/// Boundary judgment call 1); this builder never re-derives it in the island. Serialization is the caller's
/// concern — <see cref="SessionHydration"/> is emitted with <see cref="IntakeHydrationJson.Options"/> at the
/// single page-side emission point.</para>
/// </summary>
public sealed class IntakeReviewHydrationBuilder
{
    /// <summary>
    /// Projects the loaded session + reference data into the island's hydration blob. Pure over
    /// (<paramref name="session"/>, <paramref name="today"/>, <paramref name="now"/>,
    /// <paramref name="urls"/>): <paramref name="today"/> supplies expiry/default dates,
    /// <paramref name="now"/> drives the "scanned N ago" relative label, and <paramref name="urls"/>
    /// carries the pre-computed row-action endpoints.
    /// </summary>
    public SessionHydration Build(
        SessionReviewView session, DateOnly today, DateTimeOffset now, ReviewHandlerUrls urls)
    {
        var reference = session.ReferenceData;

        // Products — include defaults so the island can fill empty unit/location/expiry
        // on product re-selection (Boundary judgment call 2: form-filling from held data = UI, allowed).
        var products = reference.Products.Select(p => new ProductHydration(
            Id: p.Id.ToString(),
            Name: p.Name,
            Skus: p.Skus.Select(s => new SkuOption(s.Id.ToString(), s.Label)).ToList(),
            Defaults: new ProductDefaults(
                UnitId: p.DefaultUnitId.ToString(),
                LocationId: p.DefaultLocationId?.ToString(),
                Expiry: p.DefaultDueDays is { } n ? today.AddDays(n).ToString("yyyy-MM-dd") : null))).ToList();

        var units = reference.Units
            .Select(u => new UnitHydration(u.Id.ToString(), u.Code, u.Name)).ToList();

        var locations = reference.Locations
            .Select(l => new LocationHydration(l.Id.ToString(), l.Name)).ToList();

        var categories = reference.Categories
            .Select(c => new CategoryHydration(c.Id.ToString(), c.Name, c.Hue)).ToList();

        var stores = reference.Stores
            .Select(s => new StoreHydration(s.Id.ToString(), s.Name)).ToList();

        var unitIdByCode = reference.Units.ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
        var productNameById = reference.Products.ToDictionary(p => p.Id, p => p.Name);
        var productDefaultLocationById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultLocationId);
        var productDefaultUnitById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultUnitId);
        var productDefaultDueDaysById = reference.Products.ToDictionary(p => p.Id, p => p.DefaultDueDays);

        // Per-line: line data + server-computed prefill (Boundary judgment call 1: chain stays server-side)
        var lines = session.Lines.Select(l =>
        {
            var (prefillProductId, prefillProductName, prefillQty, prefillUnitId, prefillLocationId, prefillPrice, prefillExpiry) =
                ReviewPrefill.ComputePrefill(l, unitIdByCode, productNameById, productDefaultLocationById,
                    productDefaultUnitById, productDefaultDueDaysById, today);

            // Alternatives: only resolved catalog entries, 2+ required
            IReadOnlyList<AlternativeHydration>? alternatives = null;
            if (l.SuggestedAlternatives is { Count: >= ImportLine.MinAlternativesForSuggestion } alts)
            {
                var resolved = alts
                    .Where(a => a.ProductId is { } p && productNameById.ContainsKey(p))
                    .Select(a => new AlternativeHydration(
                        ProductId: a.ProductId!.Value.ToString(),
                        ProductName: productNameById.TryGetValue(a.ProductId!.Value, out var n) ? n : a.ProductName,
                        Confidence: a.Confidence))
                    .ToList();
                if (resolved.Count >= ImportLine.MinAlternativesForSuggestion)
                    alternatives = resolved;
            }

            return new LineHydration(
                Line: new LineSeed(
                    LineId: l.LineId.ToString(),
                    ReceiptText: l.ReceiptText,
                    Confidence: l.SuggestedConfidence.ToString(),
                    Status: l.Status.ToString(),
                    ProductId: l.ProductId?.ToString(),
                    SkuId: l.SkuId?.ToString(),
                    Quantity: l.Quantity,
                    UnitId: l.UnitId?.ToString(),
                    LocationId: l.LocationId?.ToString(),
                    ExpiryDate: l.ExpiryDate?.ToString("yyyy-MM-dd"),
                    Price: l.Price,
                    IsNewProduct: l.IsNewProduct,
                    NewProductName: l.NewProductName,
                    NewProductCategoryId: l.NewProductCategoryId?.ToString(),
                    SuggestedPrice: l.SuggestedPrice),
                Prefill: new PrefillData(
                    ProductId: prefillProductId?.ToString(),
                    ProductName: prefillProductName,
                    Quantity: prefillQty,
                    UnitId: prefillUnitId?.ToString(),
                    LocationId: prefillLocationId?.ToString(),
                    Price: prefillPrice,
                    Expiry: prefillExpiry?.ToString("yyyy-MM-dd"),
                    SkuId: l.SkuId?.ToString()),
                Alternatives: alternatives,
                Estimate: l is { ReceiptWeight: { } w, ReceiptWeightUnitLabel: { } wu, EstimatedEachCount: { } ec }
                    ? new EstimateHydration(ec, w, wu, (l.EstimatedEachConfidence ?? SuggestedConfidence.Low).ToString())
                    : null);
        }).ToList();

        return new SessionHydration(
            MerchantText: string.IsNullOrWhiteSpace(session.MerchantText) ? "Receipt" : session.MerchantText,
            SessionDate: session.CreatedAt.ToLocalTime().ToString("ddd MMM d, yyyy", CultureInfo.CurrentCulture),
            Today: today.ToString("yyyy-MM-dd"),
            CommitUrl: urls.Commit,
            DiscardUrl: urls.Discard,
            SaveLineUrl: urls.SaveLine,
            DismissLineUrl: urls.DismissLine,
            RestoreLineUrl: urls.RestoreLine,
            ReopenLineUrl: urls.Reopen,
            ConfirmLinesUrl: urls.ConfirmLines,
            CorrectHeaderUrl: urls.CorrectHeader,
            Products: products,
            Units: units,
            Locations: locations,
            Categories: categories,
            Stores: stores,
            Lines: lines,
            // Receipt-panel metadata — via tag reflects the source; the rest is present-only display data.
            ScanVia: session.SourceType == ImportSourceType.Receipt ? "photo" : "email",
            ScannedLabel: RelativeScanLabel(session.CreatedAt, now),
            StoreBranch: NullIfBlank(session.StoreBranch),
            PurchaseDate: session.PurchaseDate is { } pd
                ? pd.ToString("ddd MMM d, yyyy", CultureInfo.CurrentCulture) : null,
            PurchaseTime: session.PurchaseTime is { } pt
                ? pt.ToString("h:mm tt", CultureInfo.CurrentCulture) : null,
            // Editable-header seeds (plantry-yobz): raw machine values the island's edit controls round-trip.
            MerchantTextRaw: NullIfBlank(session.MerchantText),
            SelectedStoreId: session.SelectedStoreId?.ToString(),
            PurchaseDateRaw: session.PurchaseDate?.ToString("yyyy-MM-dd"),
            PurchaseTimeRaw: session.PurchaseTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            Subtotal: session.Subtotal,
            Tax: session.Tax,
            Total: session.Total,
            Payment: NullIfBlank(session.PaymentDescriptor),
            ReceiptNo: NullIfBlank(session.ReceiptNumber));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Humanises how long ago the receipt was scanned, for the receipt panel's meta line
    /// ("scanned just now" / "scanned 5 minutes ago" / "scanned on Jun 7, 2026"). Coarse buckets only —
    /// this is ambient display copy, not a precise timestamp.</summary>
    private static string RelativeScanLabel(DateTimeOffset scannedAt, DateTimeOffset now)
    {
        var elapsed = now - scannedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "scanned just now";
        if (elapsed < TimeSpan.FromHours(1))
        {
            var mins = (int)elapsed.TotalMinutes;
            return $"scanned {mins} minute{(mins == 1 ? "" : "s")} ago";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return $"scanned {hours} hour{(hours == 1 ? "" : "s")} ago";
        }
        return "scanned on " + scannedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
