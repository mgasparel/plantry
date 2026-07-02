using System.Text.Json;
using Plantry.Deals.Infrastructure;

namespace Plantry.Tests.Unit.Deals.Infrastructure;

/// <summary>
/// L1 tests for <see cref="FlyerSource"/>'s pure mapping methods — exercised against recorded Flipp
/// payloads with no live HTTP call (mirrors <c>GeminiReceiptParserTests</c>). Covers the happy-path map
/// (<see cref="FlyerSource.MapFlyer"/> → RawDeal[] + window + flyer_external_id + content-hash input),
/// directory search (<see cref="FlyerSource.MapDirectory"/> → candidate stores), the multi-buy price
/// parse (<see cref="FlyerSource.ParsePrice"/>), and the soft-fail paths (empty/malformed/missing payload
/// degrades to an error-carrying result and never throws — ADR-007).
/// </summary>
public sealed class FlyerSourceTests
{
    // A recorded /data payload: two merchants (one with two active flyers), each with a merchant name +
    // validity window. Shapes deliberately mix `flyers`-wrapped array and varied field spellings.
    private const string RecordedDirectory = """
        {
          "flyers": [
            { "id": 111, "merchant": "Metro",     "valid_from": "2026-06-24", "valid_to": "2026-06-30" },
            { "id": 112, "merchant": "Metro",     "valid_from": "2026-06-25", "valid_to": "2026-07-01" },
            { "id": 222, "merchant_name": "No Frills", "start_date": "2026-06-24", "end_date": "2026-06-30" },
            { "id": 333, "store_name": "" }
          ]
        }
        """;

    // A recorded single-flyer metadata object (as it appears inside /data).
    private const string RecordedFlyer = """
        { "id": 111, "merchant": "Metro", "valid_from": "2026-06-24", "valid_to": "2026-06-30" }
        """;

    // A recorded /flyers/{id}/flyer_items payload: a plain-price item, a multi-buy promo item, a
    // string-price item, and an unnamed item that must be dropped.
    private const string RecordedItems = """
        {
          "flyer_items": [
            {
              "id": 1,
              "name": "Whole Milk 2%",
              "brand_name": "Beatrice",
              "size": "4 L",
              "current_price": 5.49,
              "sale_story": "Save $1.00"
            },
            {
              "name": "Yogurt Tubs",
              "brand": "Astro",
              "sale_story": "2 for $5"
            },
            {
              "product_name": "Cheddar Cheese",
              "price": "$7.99"
            },
            {
              "brand": "NoName",
              "current_price": 1.00
            }
          ]
        }
        """;

    // ── Directory search ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MapDirectory_Returns_Distinct_Merchants_With_Stable_Refs()
    {
        var merchants = FlyerSource.MapDirectory(RecordedDirectory, nameQuery: null);

        // Metro appears in two flyers but collapses to one merchant; the blank-named flyer is dropped.
        Assert.Equal(2, merchants.Count);

        var metro = merchants.Single(m => m.Name == "Metro");
        Assert.Equal("flipp-metro", metro.ExternalRef);

        var noFrills = merchants.Single(m => m.Name == "No Frills");
        Assert.Equal("flipp-no-frills", noFrills.ExternalRef);
    }

    [Fact]
    public void MapDirectory_Filters_By_Name_Query_Case_Insensitively()
    {
        var merchants = FlyerSource.MapDirectory(RecordedDirectory, nameQuery: "metro");

        Assert.Single(merchants);
        Assert.Equal("Metro", merchants[0].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("{\"flyers\":\"not-an-array\"}")]
    public void MapDirectory_Degrades_To_Empty_On_Bad_Or_Empty_Payload(string payload)
    {
        var merchants = FlyerSource.MapDirectory(payload, nameQuery: null);
        Assert.Empty(merchants);
    }

    // ── Flyer pull mapping ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MapFlyer_Maps_Recorded_Payload_Into_RawDeals_Window_And_ExternalId()
    {
        var result = FlyerSource.MapFlyer(RecordedFlyer, RecordedItems);

        Assert.False(result.HasError);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("111", result.FlyerExternalId);

        // ValidityWindow parsed from the flyer metadata.
        Assert.NotNull(result.Window);
        Assert.Equal(new DateOnly(2026, 6, 24), result.Window!.ValidFrom);
        Assert.Equal(new DateOnly(2026, 6, 30), result.Window.ValidTo);

        // Content-hash input is the raw items payload, preserved verbatim (hashed downstream in P5-6).
        Assert.Equal(RecordedItems, result.RawContent);

        // The unnamed item is dropped; three usable deals remain.
        Assert.Equal(3, result.Deals.Count);

        var milk = result.Deals.Single(d => d.RawName == "Whole Milk 2%");
        Assert.Equal("Beatrice", milk.Brand);
        Assert.Equal("4 L", milk.Size);
        Assert.Equal(5.49m, milk.Price);
        Assert.Null(milk.Quantity);
        Assert.Equal("Save $1.00", milk.SaleStory);
        Assert.Null(milk.UnitId); // unit_id resolution deferred to P5-6 — the adapter stays catalog-free
        Assert.Equal(result.Window, milk.Window); // window copied onto each deal

        // Multi-buy promo: Price is the advertised total for Quantity units ("2 for $5" → 5 for 2).
        var yogurt = result.Deals.Single(d => d.RawName == "Yogurt Tubs");
        Assert.Equal(5m, yogurt.Price);
        Assert.Equal(2m, yogurt.Quantity);
        Assert.Equal("2 for $5", yogurt.SaleStory);

        // String-form price ("$7.99") parses to a decimal.
        var cheese = result.Deals.Single(d => d.RawName == "Cheddar Cheese");
        Assert.Equal(7.99m, cheese.Price);
    }

    [Theory]
    [InlineData("2 for $5", 5, 2)]
    [InlineData("3/$10", 10, 3)]
    [InlineData("2 FOR $4.50", 4.50, 2)]
    public void ParsePrice_Derives_Total_And_Quantity_From_Multi_Buy(string saleStory, double expectedPrice, double expectedQty)
    {
        var raw = JsonDocument.Parse($"{{\"sale_story\":\"{saleStory}\"}}").RootElement;

        var (price, quantity, story) = FlyerSource.ParsePrice(raw);

        Assert.Equal((decimal)expectedPrice, price);
        Assert.Equal((decimal)expectedQty, quantity);
        Assert.Equal(saleStory, story);
    }

    [Fact]
    public void ParsePrice_Returns_Zero_When_Price_Unknowable_But_Preserves_Sale_Story()
    {
        var raw = JsonDocument.Parse("{\"sale_story\":\"Buy one get one free\"}").RootElement;

        var (price, quantity, story) = FlyerSource.ParsePrice(raw);

        Assert.Equal(0m, price);
        Assert.Null(quantity);
        Assert.Equal("Buy one get one free", story);
    }

    // ── Soft-fail paths (never throw into the domain — ADR-007) ─────────────────────────────────────

    [Fact]
    public void MapFlyer_Soft_Fails_On_Empty_Flyer_Payload()
    {
        var result = FlyerSource.MapFlyer("   ", RecordedItems);

        Assert.True(result.HasError);
        Assert.Null(result.Window);
        Assert.Empty(result.Deals);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapFlyer_Soft_Fails_On_Malformed_Flyer_Json_Without_Throwing()
    {
        var result = FlyerSource.MapFlyer("{ not valid json ", RecordedItems);

        Assert.True(result.HasError);
        Assert.Empty(result.Deals);
        Assert.Contains("unparseable", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapFlyer_Soft_Fails_When_Window_Missing()
    {
        var noWindow = "{ \"id\": 999, \"merchant\": \"Metro\" }";

        var result = FlyerSource.MapFlyer(noWindow, RecordedItems);

        Assert.True(result.HasError);
        Assert.Null(result.Window);
        Assert.Contains("validity window", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapFlyer_Soft_Fails_On_Inverted_Window()
    {
        var inverted = "{ \"id\": 999, \"merchant\": \"Metro\", \"valid_from\": \"2026-06-30\", \"valid_to\": \"2026-06-24\" }";

        var result = FlyerSource.MapFlyer(inverted, RecordedItems);

        Assert.True(result.HasError);
        Assert.Contains("invalid validity window", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapFlyer_Succeeds_With_No_Deals_When_Items_Payload_Is_Malformed()
    {
        // The flyer metadata is valid, but the items payload is junk — the pull still succeeds (a real,
        // if empty, flyer) with zero deals rather than soft-failing the whole pull.
        var result = FlyerSource.MapFlyer(RecordedFlyer, "{ not valid json");

        Assert.False(result.HasError);
        Assert.NotNull(result.Window);
        Assert.Empty(result.Deals);
    }

    [Fact]
    public void MapFlyer_Handles_Bare_Array_Items_Payload()
    {
        var bareArray = """[ { "name": "Bread", "current_price": 2.50 } ]""";

        var result = FlyerSource.MapFlyer(RecordedFlyer, bareArray);

        Assert.False(result.HasError);
        Assert.Single(result.Deals);
        Assert.Equal("Bread", result.Deals[0].RawName);
        Assert.Equal(2.50m, result.Deals[0].Price);
    }

    // ── Failed factory ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MerchantRef_Slugs_The_Merchant_Name()
    {
        Assert.Equal("flipp-real-canadian-superstore", FlyerSource.MerchantRef("Real Canadian Superstore"));
        Assert.Equal("flipp-freshco", FlyerSource.MerchantRef("  FreshCo  "));
    }
}
