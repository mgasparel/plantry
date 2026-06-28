using Plantry.Identity.Domain;
using Plantry.Intake.Domain;
using Plantry.Inventory.Application;
using Plantry.Inventory.Domain;
using Plantry.Recipes.Application;
using Plantry.Recipes.Domain;
using Plantry.SharedKernel;
using Plantry.SharedKernel.Domain;
using Plantry.SharedKernel.Tenancy;
using InvProductStock = Plantry.Inventory.Domain.ProductStock;
using RecProductStock = Plantry.Recipes.Application.ProductStock;

namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Deterministic fixture data for the L4 cook-now picks fragment tests (plantry-81g).
///
/// Three recipes exercise the key rendering paths:
/// <list type="bullet">
///   <item>PastaCarbonara — fully cookable (100%), with a cook time, an expiring ingredient
///     (eggs) → "Use it up" badge + "Ready to cook" hint. Has a photo.</item>
///   <item>VeggieStir — partially cookable (50%), no expiring ingredients, no cook time
///     → "1 to pick up first" hint. No photo (placeholder rendered).</item>
///   <item>SmoothieBowl — not cookable (0%), fully missing ingredients
///     → "2 to pick up first" hint. No photo.</item>
/// </list>
/// The pick ordering under the cook-now selection is:
///   1. PastaCarbonara (expiring=true, pct=100) — slot 1
///   2. VeggieStir     (expiring=false, pct=50)  — slot 2
///   3. SmoothieBowl   (expiring=false, pct=0)   — slot 3
/// </summary>
public static class TodayCookNowFixture
{
    public static readonly Guid HouseholdAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099");
    private static readonly HouseholdId Household = HouseholdId.From(HouseholdAId);
    private static readonly IClock Clock = SystemClock.Instance;

    // ── Product ids ───────────────────────────────────────────────────────────

    public static readonly Guid EggId    = Guid.Parse("cc000001-0000-0000-0000-000000000001");
    public static readonly Guid FlourId  = Guid.Parse("cc000001-0000-0000-0000-000000000002");
    public static readonly Guid SpinachId= Guid.Parse("cc000001-0000-0000-0000-000000000003");
    public static readonly Guid PepperIId= Guid.Parse("cc000001-0000-0000-0000-000000000004");
    public static readonly Guid BananaId = Guid.Parse("cc000001-0000-0000-0000-000000000005");
    public static readonly Guid BerriesId= Guid.Parse("cc000001-0000-0000-0000-000000000006");
    public static readonly Guid EachUnit = Guid.Parse("cc000002-0000-0000-0000-000000000001");
    public static readonly Guid GramUnit = Guid.Parse("cc000002-0000-0000-0000-000000000002");

    // ── Recipes ───────────────────────────────────────────────────────────────

    public static IReadOnlyList<Recipe> BuildRecipes()
    {
        // PastaCarbonara: eggs (in-stock, expiring soon), flour (in-stock). Fully cookable. Has photo.
        var pasta = Recipe.Create(Household, "Pasta Carbonara", defaultServings: 2, Clock).Value;
        pasta.SetCookTime(20, Clock);
        pasta.ReplaceIngredients(
        [
            new IngredientLine(EggId,    2m,   EachUnit, null, 0),
            new IngredientLine(FlourId, 100m,  GramUnit, null, 1),
        ], Clock);
        pasta.SetPhoto([1, 2, 3], "image/jpeg", null, Clock);

        // VeggieStir: spinach (in-stock), pepper (missing). Partially cookable (50%). No photo.
        var veggie = Recipe.Create(Household, "Veggie Stir", defaultServings: 2, Clock).Value;
        veggie.ReplaceIngredients(
        [
            new IngredientLine(SpinachId, 100m, GramUnit, null, 0),
            new IngredientLine(PepperIId,   1m, EachUnit, null, 1),
        ], Clock);

        // SmoothieBowl: banana (missing), berries (missing). Not cookable (0%). No photo.
        var smoothie = Recipe.Create(Household, "Smoothie Bowl", defaultServings: 1, Clock).Value;
        smoothie.SetCookTime(5, Clock);
        smoothie.ReplaceIngredients(
        [
            new IngredientLine(BananaId,   2m,  EachUnit, null, 0),
            new IngredientLine(BerriesId, 50m,  GramUnit, null, 1),
        ], Clock);

        return [pasta, veggie, smoothie];
    }

    // ── Catalog products (for FulfillmentService via ICatalogProductReader) ───

    public static IReadOnlyDictionary<Guid, CatalogProduct> Products() =>
        new Dictionary<Guid, CatalogProduct>
        {
            [EggId]     = new(EggId,     "Eggs",    TrackStock: true, EachUnit, null, false, []),
            [FlourId]   = new(FlourId,   "Flour",   TrackStock: true, GramUnit, null, false, []),
            [SpinachId] = new(SpinachId, "Spinach", TrackStock: true, GramUnit, null, false, []),
            [PepperIId] = new(PepperIId, "Peppers", TrackStock: true, EachUnit, null, false, []),
            [BananaId]  = new(BananaId,  "Bananas", TrackStock: true, EachUnit, null, false, []),
            [BerriesId] = new(BerriesId, "Berries", TrackStock: true, GramUnit, null, false, []),
        };

    // ── Stock (for FulfillmentService via IInventoryStockReader) ─────────────
    // Eggs: in-stock, expiring 2 days out → HasIngredientExpiringSoon=true on PastaCarbonara.
    // Flour: in-stock.
    // Spinach: in-stock.
    // Pepper, Banana, Berries: no stock → Missing.

    public static IReadOnlyDictionary<Guid, RecProductStock> Stock() =>
        new Dictionary<Guid, RecProductStock>
        {
            [EggId]     = new(EggId,     10m,  EachUnit, SoonestExpiry: new DateOnly(2026, 6, 16)),
            [FlourId]   = new(FlourId,  500m,  GramUnit, SoonestExpiry: null),
            [SpinachId] = new(SpinachId, 200m, GramUnit, SoonestExpiry: null),
            // PepperIId, BananaId, BerriesId intentionally omitted → Missing
        };

    // ── Tags (empty — Today picks don't display tags) ─────────────────────────

    public static IReadOnlyList<Tag> Tags() => [];

    // ── Prices (empty — cook-now picks don't display cost) ───────────────────

    public static IReadOnlyDictionary<Guid, PricePoint> Prices() =>
        new Dictionary<Guid, PricePoint>();
}

// ── Today page infrastructure fakes ──────────────────────────────────────────

/// <summary>
/// In-memory <see cref="IProductStockRepository"/> for the Today page L4 tests.
/// Only <see cref="AnyForHouseholdAsync"/> is exercised by IndexModel.
/// </summary>
public sealed class FakeTodayStockRepository(bool hasStock) : IProductStockRepository
{
    public Task<bool> AnyForHouseholdAsync(HouseholdId householdId, CancellationToken ct = default) =>
        Task.FromResult(hasStock);
    public Task<List<InvProductStock>> ListForHouseholdAsync(HouseholdId h, CancellationToken ct = default) =>
        Task.FromResult(new List<InvProductStock>());
    public Task<InvProductStock?> FindForUpdateAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<InvProductStock?>(null);
    public Task<InvProductStock?> FindAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<InvProductStock?>(null);
    public Task<InvProductStock?> FindWithHistoryAsync(HouseholdId h, Guid p, CancellationToken ct = default) => Task.FromResult<InvProductStock?>(null);
    public Task AddAsync(InvProductStock stock, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> TryAddAndSaveAsync(InvProductStock stock, CancellationToken ct = default) => Task.FromResult(true);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default) => work(ct);
}

/// <summary>
/// In-memory <see cref="IImportSessionRepository"/> for the Today page L4 tests.
/// Only <see cref="HasPendingAsync"/> is exercised by IndexModel.
/// </summary>
public sealed class FakeTodaySessionRepository : IImportSessionRepository
{
    public Task<bool> HasPendingAsync(HouseholdId hid, CancellationToken ct = default) => Task.FromResult(false);
    public Task<List<ImportSession>> ListPendingAsync(HouseholdId hid, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
    public Task AddAsync(ImportSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportSession?>(null);
    public Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default) => Task.FromResult<ImportReceipt?>(null);
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<List<ImportSession>> ListRecentAsync(HouseholdId hid, int take = 10, CancellationToken ct = default) => Task.FromResult(new List<ImportSession>());
}

/// <summary>
/// In-memory <see cref="IHouseholdRepository"/> for the Today page L4 tests.
/// </summary>
public sealed class FakeTodayHouseholdRepository : IHouseholdRepository
{
    private static readonly IClock Clock = SystemClock.Instance;

    public Task<Household?> FindAsync(HouseholdId id, CancellationToken ct = default)
    {
        var h = Household.Create("Cook Test Household", Clock);
        return Task.FromResult<Household?>(h);
    }
    public Task AddAsync(Household household, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// In-memory <see cref="ICatalogReadFacade"/> for the Today page L4 tests.
/// Returns empty lists — InventoryQueryService uses this for expiry display labels, not needed here.
/// </summary>
public sealed class FakeTodayCatalogReadFacade : ICatalogReadFacade
{
    public Task<CatalogProductInfo?> FindProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<CatalogProductInfo?>(null);
    public Task<IReadOnlyList<CatalogProductInfo>> ListProductsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogProductInfo>>([]);
    public Task<IReadOnlyDictionary<Guid, string>> GetUnitCodesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    public Task<IReadOnlyDictionary<Guid, string>> GetLocationNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
}

/// <summary>
/// In-memory <see cref="IProductConversionProvider"/> for the Today page L4 tests.
/// Returns an identity converter for all products (ingredient unit == product unit in fixture).
/// </summary>
public sealed class FakeTodayConversionProvider : IProductConversionProvider
{
    public Task<IQuantityConverter> ForProductAsync(Guid productId, CancellationToken ct = default) =>
        Task.FromResult<IQuantityConverter>(new IdentityConverter());

    private sealed class IdentityConverter : IQuantityConverter
    {
        public Result<decimal> Convert(decimal amount, Guid fromUnitId, Guid toUnitId) => amount;
    }
}
