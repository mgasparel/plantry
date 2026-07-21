using Microsoft.Extensions.DependencyInjection;
using Plantry.Catalog.Application;
using Plantry.Deals.Application;
using Plantry.Housekeeping.Application;
using Plantry.Intake.Application;
using Plantry.Inventory.Application;
using Plantry.MealPlanning.Application;
using Plantry.Pricing.Application;
using Plantry.Recipes.Application;
using Plantry.Shopping.Application;
using Plantry.SharedKernel.Domain;
using Plantry.Web.Deals;
using Plantry.Web.Events;
using Plantry.Web.Housekeeping;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.MealPlanning;
using Plantry.Web.Pricing;
using Plantry.Web.Recipes;
using Plantry.Web.Shopping;

namespace Plantry.Composition;

/// <summary>
/// Composition-root wiring for the cross-context ACL adapters and the domain-event dispatch machinery
/// (plantry-m1u). This is the "how bounded contexts are wired together" seam, lifted out of the web/UI
/// host: <see cref="AddCrossContextAdapters"/> binds every context-application ACL port to its adapter
/// implementation, plus the <see cref="IDomainEventDispatcher"/> + interceptor pair + transactional
/// buffer. Called once from Plantry.Web's Program.cs.
/// <para>
/// Intentionally NOT registered here (they stay in the host): the feature-flagged
/// <c>IFlyerSource</c> → <c>StubFlyerSourceAdapter</c> binding (host owns the Deals:UseStubFlyerSource
/// switch and the real HttpClient alternative), the Identity read-port implementation
/// (<c>IHouseholdDirectory</c>, which lives in Plantry.Identity.Infrastructure and is ASP.NET-coupled —
/// registering it here would drag Microsoft.AspNetCore.* into this assembly), and the domain-event
/// <i>handlers</i> (not adapters). The DbContext <c>.AddInterceptors(...)</c> calls likewise stay in the
/// host, since they are DbContext configuration — they merely resolve the interceptors registered here.
/// </para>
/// </summary>
public static class CompositionServiceCollectionExtensions
{
    public static IServiceCollection AddCrossContextAdapters(this IServiceCollection services)
    {
        // Domain-event dispatch (Intake and Deals contexts, ADR-014 / plantry-jvzk). The dispatcher
        // resolves handlers reflectively; the interceptor pair + scoped buffer make dispatch
        // transaction-aware. The host wires the interceptors onto the Intake/Deals DbContexts.
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<TransactionalDomainEventBuffer>();
        services.AddScoped<DomainEventDispatchInterceptor>();
        services.AddScoped<DomainEventCommitDispatchInterceptor>();

        // Inventory → Catalog ACL (take-stock read/write over Catalog reference data).
        services.AddScoped<ITakeStockReader, TakeStockReaderAdapter>();
        services.AddScoped<ITakeStockCatalogWriter, TakeStockCatalogWriterAdapter>();

        // Pricing unit-price calculation ACL.
        services.AddScoped<IUnitPriceCalculator, UnitPriceCalculatorAdapter>();

        // Deals ACLs onto Catalog store reference data, Pricing observation write, Catalog product
        // existence, Inventory purchase-frequency, and the Shopping list writer.
        services.AddScoped<ICatalogStoreReader, CatalogStoreReaderAdapter>();
        services.AddScoped<ICatalogStoreWriter, CatalogStoreWriterAdapter>();
        services.AddScoped<IPriceObservationWriter, RecordDealObservationAdapter>();
        services.AddScoped<Plantry.Deals.Application.ICatalogProductReader, DealCatalogProductReaderAdapter>();
        services.AddScoped<IPurchaseFrequencyReader, PurchaseFrequencyReaderAdapter>();
        services.AddScoped<IDealShoppingListWriter, DealShoppingListWriterAdapter>();

        // Meal Planning ACLs onto Recipes (tags, recipe read model), Identity (household members via the
        // ASP.NET-free IHouseholdDirectory port), Catalog, Inventory, Pricing, and Shopping.
        services.AddScoped<ITagReader, TagReaderAdapter>();
        services.AddScoped<IHouseholdMemberReader, HouseholdMemberReaderAdapter>();
        services.AddScoped<IRecipeReadModel, RecipeReadModelAdapter>();
        services.AddScoped<IMealPlanCatalogProductReader, MealPlanCatalogProductReaderAdapter>();
        services.AddScoped<IMealPlanStockReader, MealPlanStockReaderAdapter>();
        services.AddScoped<IMealPlanPriceReader, MealPlanPriceReaderAdapter>();
        services.AddScoped<IMealPlanShoppingWriter, MealPlanShoppingWriterAdapter>();
        services.AddScoped<IMealPlanExpiringStockReader, MealPlanExpiringStockReaderAdapter>();
        // Fully qualified: IExpiringSoonHorizonReader + ExpiringSoonHorizonReaderAdapter names exist in
        // both the MealPlanning and Recipes namespaces.
        services.AddScoped<Plantry.MealPlanning.Application.IExpiringSoonHorizonReader,
            Plantry.Web.MealPlanning.ExpiringSoonHorizonReaderAdapter>();

        // Shopping ACLs onto Catalog, Inventory, Recipes, Meal Planning, Deals attribution, and Pricing.
        services.AddScoped<IShoppingCatalogReader, ShoppingCatalogReaderAdapter>();
        services.AddScoped<IShoppingPantryReader, ShoppingPantryReaderAdapter>();
        services.AddScoped<IShoppingRecipeReader, ShoppingRecipeReaderAdapter>();
        services.AddScoped<IShoppingMealPlanReader, ShoppingMealPlanReaderAdapter>();
        services.AddScoped<IShoppingDealAttributionReader, ShoppingDealAttributionReaderAdapter>();
        services.AddScoped<IShoppingDealReader, ShoppingDealReaderAdapter>();

        // Recipes ACLs onto Catalog (read/write + unit conversion), Inventory (stock read + consume),
        // Pricing (latest price), and Shopping (list writer).
        services.AddScoped<Plantry.Recipes.Application.ICatalogProductReader, CatalogProductReaderAdapter>();
        services.AddScoped<ICatalogWriter, CatalogWriterAdapter>();
        services.AddScoped<IUnitConverter, RecipesUnitConverterAdapter>();
        services.AddScoped<IQuantityFormatter, RecipesQuantityFormatterAdapter>();
        services.AddScoped<IInventoryStockReader, InventoryStockReaderAdapter>();
        services.AddScoped<IInventoryConsumer, InventoryConsumerAdapter>();
        services.AddScoped<IInventoryProducer, InventoryProducerAdapter>();
        services.AddScoped<Plantry.Recipes.Application.IExpiringSoonHorizonReader,
            Plantry.Web.Recipes.ExpiringSoonHorizonReaderAdapter>();
        services.AddScoped<IPriceReader, PriceReaderAdapter>();
        services.AddScoped<IShoppingListWriter, ShoppingListWriterAdapter>();
        // Recipes → Identity assistive-AI gate ACL (plantry-qll2.2): the edit-moment AI features
        // (tag suggestions today; nudge/conversion as qll2.3/qll2.4 land) read the household toggle
        // through this port rather than depending on Identity directly.
        services.AddScoped<IAiAssistanceGateReader, AiAssistanceGateReaderAdapter>();

        // Intake ACLs onto Catalog (create product, ensure purchase store, seed conversion), Inventory
        // (add stock), and Pricing (record price) — the receipt-commit cross-context write seams.
        services.AddScoped<ICreateProductPort, CreateProductAdapter>();
        services.AddScoped<IAddStockPort, AddStockAdapter>();
        services.AddScoped<IRecordPricePort, RecordPriceAdapter>();
        services.AddScoped<IEnsurePurchaseStorePort, EnsurePurchaseStoreAdapter>();
        services.AddScoped<ISeedConversionPort, SeedConversionAdapter>();

        // Housekeeping (tidy-up.md T4/T8) — v1 ships D1 + D2, the conversion-gap detector family.
        // Registered as IProblemDetector so GetTidyUpPageQuery discovers every implementation via
        // IEnumerable<IProblemDetector> — adding a detector is one class + one line here, no other edits.
        services.AddScoped<IProblemDetector, StockUnitUnconvertibleDetector>();
        services.AddScoped<IProblemDetector, RecipeConversionGapDetector>();

        return services;
    }
}
