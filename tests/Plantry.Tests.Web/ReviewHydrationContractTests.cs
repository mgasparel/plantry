using System.Text.Json;
using Plantry.Tests.Web.Infrastructure;
using Plantry.Web.Pages.Intake;

namespace Plantry.Tests.Web;

/// <summary>
/// Consumer-contract test for the Intake review island's hydration payload (plantry-eoj5 Phase B).
///
/// The server emits this shape (Review.cshtml.cs::BuildHydrationJson → the named DTOs in
/// ReviewHydration.cs) and the island parses it by hand against a JSDoc @typedef block
/// (intake-review.js). No compiler spans that seam. This test pins the EXACT camelCase key set at
/// every nesting level, so dropping or renaming a field the island reads fails here — loudly,
/// server-side — instead of surfacing as `undefined` in the browser.
///
/// Complements ReviewFragmentSnapshotTests, which pins VALUES from the live endpoint: this pins the
/// full key SURFACE deterministically, including shapes the integration fixture leaves empty (skus).
/// Serialization uses the same <see cref="IntakeHydrationJson.Options"/> the page emits with.
/// </summary>
public sealed class ReviewHydrationContractTests
{
    private static JsonElement Serialize(SessionHydration h) =>
        JsonDocument.Parse(JsonSerializer.Serialize(h, IntakeHydrationJson.Options)).RootElement;

    /// <summary>A fully-populated payload — every nested shape present (a product with a sku, a line
    /// with a prefill and an alternative) so the key assertions cover the whole contract surface.</summary>
    private static SessionHydration Sample() => new(
        MerchantText: "Receipt",
        SessionDate: "Mon Jun 15, 2026",
        Today: "2026-06-15",
        CommitUrl: "/Intake/Review/1?handler=Commit",
        DiscardUrl: "/Intake/Review/1?handler=Discard",
        SaveLineUrl: "/Intake/Review/1?handler=SaveLine",
        DismissLineUrl: "/Intake/Review/1?handler=DismissLine",
        RestoreLineUrl: "/Intake/Review/1?handler=RestoreLine",
        ReopenLineUrl: "/Intake/Review/1?handler=ReopenLine",
        ConfirmLinesUrl: "/Intake/Review/1?handler=ConfirmLines",
        CorrectHeaderUrl: "/Intake/Review/1?handler=CorrectHeader",
        Products:
        [
            new ProductHydration(
                Id: "p1", Name: "Milk",
                Skus: [new SkuOption("sku1", "2L carton")],
                Defaults: new ProductDefaults(UnitId: "u1", LocationId: "loc1", Expiry: "2026-06-22")),
        ],
        Units: [new UnitHydration("u1", "L", "Litre")],
        Locations: [new LocationHydration("loc1", "Fridge")],
        Categories: [new CategoryHydration("cat1", "Dairy", 200)],
        Stores: [new StoreHydration("store1", "Test Grocer")],
        Lines:
        [
            new LineHydration(
                Line: new LineSeed(
                    LineId: "l1", ReceiptText: "WHOLE MILK 2L", Confidence: "High",
                    Status: "Pending", ProductId: "p1", SkuId: "sku1", Quantity: 2m, UnitId: "u1",
                    LocationId: "loc1", ExpiryDate: "2026-06-22", Price: 3.99m, IsNewProduct: false,
                    NewProductName: null, NewProductCategoryId: null, SuggestedPrice: 3.99m),
                Prefill: new PrefillData(
                    ProductId: "p1", ProductName: "Milk", Quantity: 2m, UnitId: "u1", LocationId: "loc1",
                    Price: 3.99m, Expiry: "2026-06-22", SkuId: "sku1"),
                Alternatives: [new AlternativeHydration("p2", "Cheddar, Sharp", 0.72m)],
                Estimate: new EstimateHydration(EachCount: 7m, Weight: 1.34m, WeightUnit: "lb", Confidence: "High")),
        ],
        ScanVia: "photo",
        ScannedLabel: "scanned just now",
        StoreBranch: "42 Market St",
        PurchaseDate: "Sun Jun 15, 2026",
        PurchaseTime: "2:30 PM",
        MerchantTextRaw: "Test Grocer",
        SelectedStoreId: "store1",
        PurchaseDateRaw: "2026-06-15",
        PurchaseTimeRaw: "14:30",
        Subtotal: 40.00m,
        Tax: 2.00m,
        Total: 42.00m,
        Payment: "VISA ****4471 APPROVED",
        ReceiptNo: "TXN 0472 118");

    [Fact]
    public void Root_has_exact_island_key_set()
    {
        HydrationContract.AssertKeys(Serialize(Sample()),
            "merchantText", "sessionDate", "today",
            "commitUrl", "discardUrl", "saveLineUrl", "dismissLineUrl", "restoreLineUrl", "reopenLineUrl", "confirmLinesUrl", "correctHeaderUrl",
            "products", "units", "locations", "categories", "stores", "lines",
            "scanVia", "scannedLabel", "storeBranch", "purchaseDate", "purchaseTime",
            "merchantTextRaw", "selectedStoreId", "purchaseDateRaw", "purchaseTimeRaw",
            "subtotal", "tax", "total", "payment", "receiptNo");
    }

    [Fact]
    public void Product_and_nested_shapes_have_exact_keys()
    {
        var product = Serialize(Sample()).GetProperty("products")[0];
        HydrationContract.AssertKeys(product, "id", "name", "skus", "defaults");
        HydrationContract.AssertKeys(product.GetProperty("defaults"), "unitId", "locationId", "expiry");
        HydrationContract.AssertKeys(product.GetProperty("skus")[0], "id", "label");
    }

    [Fact]
    public void Reference_collections_have_exact_keys()
    {
        var root = Serialize(Sample());
        HydrationContract.AssertKeys(root.GetProperty("units")[0], "id", "code", "name");
        HydrationContract.AssertKeys(root.GetProperty("locations")[0], "id", "name");
        HydrationContract.AssertKeys(root.GetProperty("categories")[0], "id", "name", "hue");
        HydrationContract.AssertKeys(root.GetProperty("stores")[0], "id", "name");
    }

    [Fact]
    public void Line_prefill_and_alternative_have_exact_keys()
    {
        var item = Serialize(Sample()).GetProperty("lines")[0];
        HydrationContract.AssertKeys(item, "line", "prefill", "alternatives", "estimate");
        HydrationContract.AssertKeys(item.GetProperty("line"),
            "lineId", "receiptText", "confidence", "status",
            "productId", "skuId", "quantity", "unitId", "locationId",
            "expiryDate", "price", "isNewProduct", "newProductName",
            "newProductCategoryId", "suggestedPrice");
        HydrationContract.AssertKeys(item.GetProperty("prefill"),
            "productId", "productName", "quantity", "unitId", "locationId",
            "price", "expiry", "skuId");
        HydrationContract.AssertKeys(item.GetProperty("alternatives")[0], "productId", "productName", "confidence");
        HydrationContract.AssertKeys(item.GetProperty("estimate"), "eachCount", "weight", "weightUnit", "confidence");
    }
}
