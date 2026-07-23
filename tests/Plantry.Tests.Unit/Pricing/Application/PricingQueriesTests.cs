using Plantry.Pricing.Application;
using Plantry.Pricing.Domain;
using Plantry.SharedKernel;

namespace Plantry.Tests.Unit.Pricing.Application;

/// <summary>
/// Read-model composition tests (DM-17): cheapest-active-deal window/MIN semantics and the
/// effective-price fallback (deal else latest purchase), evaluated against a supplied "today".
/// The DB-level source filtering and MIN ordering are proven end-to-end in the integration suite;
/// here we pin the query surface and the compose logic in <see cref="PricingQueries"/>.
/// </summary>
public sealed class PricingQueriesTests
{
    private static readonly HouseholdId Household = HouseholdId.New();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid UnitId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid SourceRef = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 7, 4);

    private static PriceObservation Deal(decimal unitPrice, DateOnly from, DateOnly to, decimal price = 5m) =>
        PriceObservation.Record(Household, ProductId, null, price, 1m, UnitId, unitPrice,
            PriceSource.Deal, "Flyer", SourceRef, DateTimeOffset.UtcNow, UserId,
            validFrom: from, validTo: to);

    private static PriceObservation Purchase(decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(Household, ProductId, null, unitPrice, 1m, UnitId, unitPrice,
            PriceSource.Purchase, "Superstore", SourceRef, observedAt, UserId);

    private static PriceObservation Manual(decimal unitPrice, DateTimeOffset observedAt) =>
        PriceObservation.Record(Household, ProductId, null, unitPrice, 1m, UnitId, unitPrice,
            PriceSource.Manual, null, null, observedAt, UserId);

    /// <summary>A deal confirmed without a pack size (DM-17): <c>unitId = Guid.Empty</c>, <c>unitPrice</c>
    /// null — the exact shape <c>RecordDealObservationAdapter</c> writes.</summary>
    private static PriceObservation UnitlessDeal(decimal price, DateOnly from, DateOnly to) =>
        PriceObservation.Record(Household, ProductId, null, price, 1m, Guid.Empty, unitPrice: null,
            PriceSource.Deal, "Flyer", SourceRef, DateTimeOffset.UtcNow, UserId,
            validFrom: from, validTo: to);

    [Fact]
    public async Task CheapestActiveDeal_Returns_Min_UnitPrice_In_Window()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Deal(3.00m, new(2026, 7, 1), new(2026, 7, 7)));
        repo.Items.Add(Deal(2.00m, new(2026, 7, 1), new(2026, 7, 7))); // cheapest, active
        repo.Items.Add(Deal(1.00m, new(2026, 6, 1), new(2026, 6, 7))); // cheaper but expired
        var queries = new PricingQueries(repo);

        var result = await queries.CheapestActiveDealAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(2.00m, result.UnitPrice);
    }

    [Fact]
    public async Task CheapestActiveDeal_Returns_Null_When_No_Deal_Active()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Deal(1.00m, new(2026, 6, 1), new(2026, 6, 7))); // expired
        var queries = new PricingQueries(repo);

        var result = await queries.CheapestActiveDealAsync(ProductId, Today);

        Assert.Null(result);
    }

    [Fact]
    public async Task EffectivePrice_Prefers_Active_Deal_Over_Latest_Purchase()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(4.00m, DateTimeOffset.UtcNow));
        repo.Items.Add(Deal(2.50m, new(2026, 7, 1), new(2026, 7, 7)));
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Deal, result.Source);
        Assert.Equal(2.50m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectivePrice_Falls_Back_To_Latest_Purchase_When_No_Active_Deal()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(4.00m, DateTimeOffset.UtcNow.AddDays(-2)));
        repo.Items.Add(Purchase(3.50m, DateTimeOffset.UtcNow.AddDays(-1))); // latest purchase
        repo.Items.Add(Deal(2.00m, new(2026, 6, 1), new(2026, 6, 7))); // expired deal ignored
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Purchase, result.Source);
        Assert.Equal(3.50m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectivePrice_Falls_Back_To_Latest_Manual_When_No_Purchase_Or_Active_Deal()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Manual(2.99m, DateTimeOffset.UtcNow));
        repo.Items.Add(Deal(2.00m, new(2026, 6, 1), new(2026, 6, 7))); // expired deal ignored
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Manual, result.Source);
        Assert.Equal(2.99m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectivePrice_Prefers_Newer_Manual_Over_Older_Purchase()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(4.00m, DateTimeOffset.UtcNow.AddDays(-2)));
        repo.Items.Add(Manual(3.50m, DateTimeOffset.UtcNow.AddDays(-1))); // newer — a manual estimate is
        // superseded (and supersedes) purely on observed_at, matching pricing.md's "emergent, free" note.
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Manual, result.Source);
        Assert.Equal(3.50m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectivePrice_Returns_Null_When_No_Observations()
    {
        var repo = new FakePriceObservationRepository();
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.Null(result);
    }

    // ── plantry-pxjp: EffectiveCostablePriceAsync skips unit-less deals for costing ─────────────────

    [Fact]
    public async Task EffectiveCostablePrice_Skips_Unitless_Active_Deal_Falls_Back_To_Latest_Purchase()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(3.00m, DateTimeOffset.UtcNow.AddDays(-1))); // e.g. broccoli, $3.00/ea
        repo.Items.Add(UnitlessDeal(2.49m, new(2026, 7, 1), new(2026, 7, 7))); // active, cheaper, no unit

        var queries = new PricingQueries(repo);

        var result = await queries.EffectiveCostablePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Purchase, result.Source);
        Assert.Equal(3.00m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectiveCostablePrice_Prefers_Fully_Specified_Active_Deal_Over_Purchase()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(4.00m, DateTimeOffset.UtcNow.AddDays(-1)));
        repo.Items.Add(Deal(2.50m, new(2026, 7, 1), new(2026, 7, 7))); // active, usable unit — should win

        var queries = new PricingQueries(repo);

        var result = await queries.EffectiveCostablePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Deal, result.Source);
        Assert.Equal(2.50m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectiveCostablePrice_Returns_Null_When_Only_An_Unitless_Deal_Exists()
    {
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(UnitlessDeal(2.49m, new(2026, 7, 1), new(2026, 7, 7))); // active, but no purchase to fall back to

        var queries = new PricingQueries(repo);

        var result = await queries.EffectiveCostablePriceAsync(ProductId, Today);

        Assert.Null(result);
    }

    [Fact]
    public async Task EffectiveCostablePrice_Falls_Back_When_Active_Deal_Has_Null_UnitPrice_But_A_Real_Unit()
    {
        // Degenerate case: a unit id is present but the unit-price calculator soft-failed (null UnitPrice).
        // Still not costable — the conversion basis (a derived per-unit price) is missing either way.
        var repo = new FakePriceObservationRepository();
        repo.Items.Add(Purchase(3.00m, DateTimeOffset.UtcNow.AddDays(-1)));
        var degenerateDeal = PriceObservation.Record(Household, ProductId, null, 2.49m, 1m, UnitId, unitPrice: null,
            PriceSource.Deal, "Flyer", SourceRef, DateTimeOffset.UtcNow, UserId,
            validFrom: new(2026, 7, 1), validTo: new(2026, 7, 7));
        repo.Items.Add(degenerateDeal);

        var queries = new PricingQueries(repo);

        var result = await queries.EffectiveCostablePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(PriceSource.Purchase, result.Source);
        Assert.Equal(3.00m, result.UnitPrice);
    }

    // ── ADR-023 A7: PricingQueries never surfaces a superseded observation ──────────────────────────

    [Fact]
    public async Task LatestPurchasePrice_Excludes_A_Superseded_Purchase_Even_Though_It_Is_The_Newest_Row()
    {
        var repo = new FakePriceObservationRepository();
        var original = Purchase(4.00m, DateTimeOffset.UtcNow.AddDays(-1));
        repo.Items.Add(original);
        var amendment = PriceObservation.RecordAmendment(original, correctedQuantity: 3m, unitPrice: 1.33m, UserId);
        original.Supersede(amendment.Id);
        repo.Items.Add(amendment);
        var queries = new PricingQueries(repo);

        var result = await queries.LatestPurchasePriceAsync(ProductId);

        Assert.NotNull(result);
        Assert.Equal(amendment.Id, result.Id);
        Assert.Equal(1.33m, result.UnitPrice);
    }

    [Fact]
    public async Task CheapestActiveDeal_Excludes_A_Superseded_Deal_Even_Though_It_Would_Otherwise_Win()
    {
        var repo = new FakePriceObservationRepository();
        var cheapButSuperseded = Deal(1.00m, new(2026, 7, 1), new(2026, 7, 7));
        repo.Items.Add(cheapButSuperseded);
        var replacement = PriceObservation.RecordAmendment(cheapButSuperseded, correctedQuantity: 1m, unitPrice: 5.00m, UserId);
        cheapButSuperseded.Supersede(replacement.Id);
        repo.Items.Add(replacement);
        repo.Items.Add(Deal(2.00m, new(2026, 7, 1), new(2026, 7, 7))); // live, dearer — must win
        var queries = new PricingQueries(repo);

        var result = await queries.CheapestActiveDealAsync(ProductId, Today);

        Assert.NotNull(result);
        Assert.Equal(2.00m, result.UnitPrice);
    }

    [Fact]
    public async Task EffectivePrice_Falls_Back_Past_A_Superseded_Purchase_To_An_Older_Live_One()
    {
        var repo = new FakePriceObservationRepository();
        var older = Purchase(4.00m, DateTimeOffset.UtcNow.AddDays(-2));
        repo.Items.Add(older);
        var newer = Purchase(3.50m, DateTimeOffset.UtcNow.AddDays(-1));
        var amendment = PriceObservation.RecordAmendment(newer, correctedQuantity: 3m, unitPrice: 1.00m, UserId);
        newer.Supersede(amendment.Id);
        repo.Items.Add(newer);
        repo.Items.Add(amendment);
        var queries = new PricingQueries(repo);

        var result = await queries.EffectivePriceAsync(ProductId, Today);

        Assert.NotNull(result);
        // The live amendment (newest, un-superseded) wins over the older purchase — the superseded
        // `newer` row itself must never surface.
        Assert.Equal(amendment.Id, result.Id);
        Assert.Equal(1.00m, result.UnitPrice);
    }
}
