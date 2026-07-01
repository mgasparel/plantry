using Plantry.Intake.Application;
using Plantry.Intake.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Builds one deterministic <see cref="ImportSession"/> (status Ready) whose lines exercise every review-row
/// state and confidence variant, plus the matching reference data, so the rendered fragments are fully
/// determined by code (line GUIDs are random but get scrubbed in the snapshots).
///
/// Line layout:
///   1. matched      — Pending + High         (AI matched an existing product, badge "Matched")
///   2. unmatched    — Pending + None         (no match; badge hidden, drawer open)
///   3. low-conf     — Pending + Low           (likely match to check; unmatched-styled, badge hidden)
///   4. confirmed    — Confirmed (existing)    (resolved to an existing product; badge from confidence)
///   5. new-product  — Confirmed (ConfirmAsNew) (resolved as a brand-new product; "· new product")
///   6. dismissed    — Dismissed               ("Add anyway")
///   7. committed    — Committed               (locked, "Added")
///
/// Confidence badge variants (badge only renders for non-unmatched rows) are covered by the matched (High)
/// and confirmed lines (we add Confirmed lines carrying High / Low / None so all three badges render).
/// </summary>
public static class ReviewSessionFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid HouseholdBId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // Reference-data ids the resolved lines point at (kept fixed so the selected <option> is stable).
    public static readonly Guid MilkProductId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid BreadProductId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid EggsProductId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid EachUnitId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid LitreUnitId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid FridgeLocationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid DairyCategoryId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    // Additional product ids for the "Did you mean" alternatives test line
    public static readonly Guid CheddarMildId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    public static readonly Guid CheddarSharpId = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222");
    public static readonly Guid CheddarMarbleId = Guid.Parse("aaaaaaaa-3333-3333-3333-333333333333");

    private static readonly DateOnly Expiry = new(2026, 7, 1);

    /// <summary>The pinned "today" date used by <see cref="FixedClock"/> in snapshot tests — the clock
    /// returns this date so the product-default expiry prefill (today + DefaultDueDays) embedded in
    /// Alpine x-data is stable across calendar days.</summary>
    public static readonly DateOnly SnapshotDate = new(2026, 6, 15);

    public static ReviewReferenceData ReferenceData() => new(
        Products:
        [
            new ReviewProductOption(MilkProductId, "Milk", "L", DefaultLocationId: FridgeLocationId, Skus: [],
                DefaultUnitId: LitreUnitId, DefaultDueDays: 7),
            new ReviewProductOption(BreadProductId, "Bread", "ea", DefaultLocationId: null, Skus: [],
                DefaultUnitId: EachUnitId, DefaultDueDays: null),
            new ReviewProductOption(EggsProductId, "Eggs", "ea", DefaultLocationId: FridgeLocationId, Skus: [],
                DefaultUnitId: EachUnitId, DefaultDueDays: 21),
            new ReviewProductOption(CheddarMildId, "Cheddar, Mild", "ea", DefaultLocationId: FridgeLocationId, Skus: [],
                DefaultUnitId: EachUnitId, DefaultDueDays: 30),
            new ReviewProductOption(CheddarSharpId, "Cheddar, Sharp", "ea", DefaultLocationId: FridgeLocationId, Skus: [],
                DefaultUnitId: EachUnitId, DefaultDueDays: 30),
            new ReviewProductOption(CheddarMarbleId, "Cheddar, Marble", "ea", DefaultLocationId: FridgeLocationId, Skus: [],
                DefaultUnitId: EachUnitId, DefaultDueDays: 30),
        ],
        Units:
        [
            new ReviewUnitOption(EachUnitId, "ea", "Each"),
            new ReviewUnitOption(LitreUnitId, "L", "Litre"),
        ],
        Locations:
        [
            new ReviewLocationOption(FridgeLocationId, "Fridge"),
        ],
        Categories:
        [
            new ReviewCategoryOption(DairyCategoryId, "Dairy"),
        ]);

    /// <summary>Builds the fixed Ready session for the given household. Each line is driven to its target
    /// state purely through the domain API so the snapshot reflects real aggregate behaviour.</summary>
    public static ImportSession Build(Guid householdId)
    {
        var clock = SystemClock.Instance;
        var session = ImportSession.Start(
            HouseholdId.From(householdId), ImportSourceType.Receipt, userId: Guid.Empty, clock);

        var matched = session.AddLine(1, "WHOLE MILK 2L", SuggestedConfidence.High, rawPayload: null,
            suggestedProductId: MilkProductId, suggestedProductName: "Milk",
            suggestedQuantity: 2m, suggestedUnitLabel: "L", suggestedPrice: 3.99m);
        var unmatched = session.AddLine(2, "MYSTERY ITEM XZ", SuggestedConfidence.None, rawPayload: null);
        var lowConf = session.AddLine(3, "ORG BREAD LOAF", SuggestedConfidence.Low, rawPayload: null);
        var confirmedHigh = session.AddLine(4, "FREE RANGE EGGS", SuggestedConfidence.High, rawPayload: null);
        var newProduct = session.AddLine(5, "ARTISAN SOURDOUGH", SuggestedConfidence.None, rawPayload: null);
        var dismissed = session.AddLine(6, "PLASTIC BAG", SuggestedConfidence.None, rawPayload: null);
        var committed = session.AddLine(7, "BUTTER 250G", SuggestedConfidence.Low, rawPayload: null);
        // Line with AI alternatives to exercise the "Did you mean" strip in the review drawer.
        session.AddLine(8, "CHEDDAR BLK 400G", SuggestedConfidence.Low, rawPayload: null,
            suggestedProductId: CheddarMildId, suggestedProductName: "Cheddar, Mild",
            suggestedQuantity: 1m, suggestedUnitLabel: "ea", suggestedPrice: 6.75m,
            suggestedAlternatives:
            [
                new AlternativeCandidate(CheddarSharpId, "Cheddar, Sharp", 0.72m),
                new AlternativeCandidate(CheddarMarbleId, "Cheddar, Marble", 0.53m),
            ]);

        // The session must be Ready before the page will render it. Metadata drives the receipt panel.
        session.MarkReady("Test Grocer", clock.UtcNow, new ReceiptMetadata(
            StoreBranch: "42 Market St",
            PurchaseDate: new DateOnly(2026, 6, 15),
            PurchaseTime: new TimeOnly(14, 30),
            Subtotal: 40.00m,
            Tax: 2.00m,
            Total: 42.00m,
            PaymentDescriptor: "VISA ****4471 APPROVED",
            ReceiptNumber: "TXN 0472 118"));

        // matched + unmatched + lowConf stay Pending (their state is derived from confidence).

        // confirmed-against-existing (High badge shows once resolved)
        confirmedHigh.Confirm(EggsProductId, skuId: null, quantity: 12m, EachUnitId, FridgeLocationId, Expiry, price: 4.50m);

        // confirmed-as-new-product
        newProduct.ConfirmAsNew("Sourdough Loaf", DairyCategoryId, quantity: 1m, EachUnitId, FridgeLocationId, Expiry, price: 5.25m);

        // dismissed
        dismissed.Dismiss();

        // committed: confirm then mark committed (a committed line can coexist with a Ready session — resumability)
        committed.Confirm(MilkProductId, skuId: null, quantity: 1m, LitreUnitId, FridgeLocationId, Expiry, price: 2.99m);
        committed.MarkCommitted(journalId: Guid.Parse("99999999-9999-9999-9999-999999999999"), priceObservationId: null);

        return session;
    }
}
