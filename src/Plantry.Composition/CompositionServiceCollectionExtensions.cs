using Microsoft.Extensions.DependencyInjection;
using Plantry.Pantry.Application;
using Plantry.Market.Application;
using Plantry.Intake.Application;
using Plantry.Planning.Application;
using Plantry.Recipes.Application;
using Plantry.Web;
using Plantry.Web.Deals;
using Plantry.Web.Intake;
using Plantry.Web.Inventory;
using Plantry.Web.MealPlanning;
using Plantry.Web.Pricing;
using Plantry.Web.Recipes;
using Plantry.Web.Shopping;

namespace Plantry.Composition;

/// <summary>
/// Composition-root wiring for the cross-context ACL adapters (plantry-m1u). This is the "how bounded
/// contexts are wired together" seam, lifted out of the web/UI host: <see cref="AddCrossContextAdapters"/>
/// binds every context-application ACL port to its adapter implementation. Called once from
/// Plantry.Web's Program.cs.
/// <para>
/// Intentionally NOT registered here (they stay in the host): the feature-flagged
/// <c>IFlyerSource</c> → <c>StubFlyerSourceAdapter</c> binding (host owns the Deals:UseStubFlyerSource
/// switch and the real HttpClient alternative), and the Identity read-port implementation
/// (<c>IHouseholdDirectory</c>, which lives in Plantry.Identity.Infrastructure and is ASP.NET-coupled —
/// registering it here would drag Microsoft.AspNetCore.* into this assembly).
/// </para>
/// </summary>
public static class CompositionServiceCollectionExtensions
{
    public static IServiceCollection AddCrossContextAdapters(this IServiceCollection services)
    {
        // Take-stock read/write facade over Catalog reference data (formerly an Inventory → Catalog ACL;
        // both halves are intra-context since the Pantry merge, ADR-024 plantry-g3da.6 — the adapters now
        // live directly in Plantry.Pantry.Application rather than bridging two assemblies).
        services.AddScoped<ITakeStockReader, TakeStockReaderAdapter>();
        services.AddScoped<ITakeStockCatalogWriter, TakeStockCatalogWriterAdapter>();

        // Catalog → Identity ACL (plantry-hh1f): ExpiryDefaultResolver's freeze/thaw fallback reads the
        // household-wide defaults through this port instead of Catalog depending on Identity directly.
        // HouseholdExpiryDefaultsAccessor is the per-request cache the adapter reads through instead of
        // calling IHouseholdExpiryDefaults.GetAsync directly (plantry-hw39, absorbing plantry-rsy1) — its
        // Scoped lifetime is tenant-load-bearing (one household's defaults per request, never leaked
        // across households), so it must be registered here where CompositionRegistrationLifetimeTests
        // can sweep it, not in the host.
        services.AddScoped<HouseholdExpiryDefaultsAccessor>();
        services.AddScoped<IHouseholdExpiryDefaultsReader, HouseholdExpiryDefaultsReaderAdapter>();
        // Per-request cache over IUnitRepository's unit codes (plantry-47tc, plantry-hw39 code review):
        // CatalogReadFacade.FindProductAsync loaded the whole units table per call, and
        // InventoryStockReaderAdapter already calls FindProductAsync in a per-product loop, so a
        // recipe/meal-plan fulfilment read multiplied units reads by product count. Scoped lifetime is
        // tenant-load-bearing (units are household reference data; never leaked across households), so
        // it must be registered here where CompositionRegistrationLifetimeTests can sweep it.
        services.AddScoped<UnitCodesAccessor>();
        // Per-request cache over IDisplayCurrency (plantry-2x6e.2, relocated plantry-47tc absorbing
        // plantry-x9vm): same Scoped/tenant-load-bearing shape as the two accessors above, but its
        // consumer is the presentation edge (MoneyDisplay) rather than an Inventory/Catalog ACL adapter —
        // it lived in the Plantry.Web host outside CompositionRegistrationLifetimeTests' sweep until now.
        // Namespace stays Plantry.Web (project convention: Composition adapters keep their original
        // Plantry.Web.* namespace), so consumer using-directives are unaffected by the move.
        services.AddScoped<DisplayCurrencyAccessor>();

        // Pricing unit-price calculation ACL.
        services.AddScoped<IUnitPriceCalculator, UnitPriceCalculatorAdapter>();

        // Deals ACLs onto Catalog store reference data, Catalog product existence, Inventory
        // purchase-frequency, and the Shopping list writer. The former Deals→Pricing observation-write
        // seam (RecordDealObservationAdapter) is gone — ConfirmDeal now calls RecordObservationCommand
        // directly, both halves being intra-context since the Market merge (ADR-024).
        services.AddScoped<ICatalogStoreReader, CatalogStoreReaderAdapter>();
        services.AddScoped<ICatalogStoreWriter, CatalogStoreWriterAdapter>();
        services.AddScoped<Plantry.Market.Application.ICatalogProductReader, DealCatalogProductReaderAdapter>();
        services.AddScoped<IPurchaseFrequencyReader, PurchaseFrequencyReaderAdapter>();
        services.AddScoped<IDealShoppingListWriter, DealShoppingListWriterAdapter>();

        // Meal Planning ACLs onto Recipes (tags, recipe read model), Identity (household members via the
        // ASP.NET-free IHouseholdDirectory port), Catalog, Inventory, Pricing, and Shopping.
        services.AddScoped<ITagReader, TagReaderAdapter>();
        services.AddScoped<Plantry.Planning.Application.IHouseholdMemberReader,
            Plantry.Web.MealPlanning.HouseholdMemberReaderAdapter>();
        services.AddScoped<IRecipeReadModel, RecipeReadModelAdapter>();
        services.AddScoped<IMealPlanCatalogProductReader, MealPlanCatalogProductReaderAdapter>();
        services.AddScoped<IMealPlanStockReader, MealPlanStockReaderAdapter>();
        services.AddScoped<IMealPlanPriceReader, MealPlanPriceReaderAdapter>();
        // Product-dish costing unit conversion (plantry-9n7l): converts a price observation's unit
        // onto a product's default unit before PlanCostingService multiplies by Servings — mirrors
        // Recipes' IUnitConverter wiring above, a separate MealPlanning-owned copy (DM-3).
        services.AddScoped<IMealPlanUnitConverter, MealPlanUnitConverterAdapter>();
        // The former IMealPlanShoppingWriter ACL (MealPlanShoppingWriterAdapter) is gone — ShopForWeekService
        // now calls Shopping's AddItemCommand directly, both halves being intra-context since the Planning
        // merge (ADR-024, plantry-g3da.5).
        services.AddScoped<IMealPlanExpiringStockReader, MealPlanExpiringStockReaderAdapter>();
        // Cook-status read port (plantry-0eut): joins Recipes CookEvent + Inventory journal — neither
        // context depends on the other or on MealPlanning (Gate 2); this is the composition-root join.
        services.AddScoped<IMealPlanCookStatusReader, MealPlanCookStatusReaderAdapter>();
        // Product-dish Eat/Undo write port (plantry-zcbx): consumes/compensates via Inventory's
        // single consumption primitive, stamped SourceType.Eat + SourceRef = plannedDishId — the
        // journal rows IMealPlanCookStatusReader nets to derive the eaten state above.
        services.AddScoped<IMealPlanEatWriter, MealPlanEatWriterAdapter>();
        // Fully qualified: IExpiringSoonHorizonReader + ExpiringSoonHorizonReaderAdapter names exist in
        // both the MealPlanning and Recipes namespaces.
        services.AddScoped<Plantry.Planning.Application.IExpiringSoonHorizonReader,
            Plantry.Web.MealPlanning.ExpiringSoonHorizonReaderAdapter>();

        // Shopping ACLs onto Catalog, Inventory, Recipes, Meal Planning, Deals attribution, and Pricing.
        services.AddScoped<IShoppingCatalogReader, ShoppingCatalogReaderAdapter>();
        services.AddScoped<IShoppingPantryReader, ShoppingPantryReaderAdapter>();
        services.AddScoped<IShoppingRecipeReader, ShoppingRecipeReaderAdapter>();
        // The former IShoppingMealPlanReader ACL (ShoppingMealPlanReaderAdapter) is gone — ShoppingListQueryService
        // now resolves MealPlan slot labels directly via IMealPlanRepository.FindSlotLabelsAsync (registered
        // above), both halves being intra-context since the Planning merge (ADR-024, plantry-g3da.5).
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
        // Recipes → Identity household-member directory ACL (plantry-zlwp.1): the per-rating-member
        // breakdown popover's display-name/initials source, a Recipes-local copy of the same
        // IHouseholdDirectory-backed adapter shape MealPlanning uses above (DM-3).
        services.AddScoped<Plantry.Recipes.Application.IHouseholdMemberReader,
            Plantry.Web.Recipes.HouseholdMemberReaderAdapter>();
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
        // ADR-023 A10 (plantry-hitc) — the amendment leg's own ACLs onto Inventory/Pricing, mirroring
        // AddStockAdapter/RecordPriceAdapter above but Result-returning rather than throw-on-failure (the
        // amendment guards are expected, user-facing outcomes, not aborts).
        services.AddScoped<IAmendStockPort, AmendStockAdapter>();
        services.AddScoped<IAmendPricePort, AmendPriceAdapter>();

        // Inventory → Intake/Recipes ACL (receipt-intake-history.md H4): the pantry History grid's
        // provenance chip. Inventory itself takes no dependency on either context — this adapter is the
        // composition-root join, same seam ShoppingRecipeReaderAdapter plays for Shopping.
        services.AddScoped<IStockProvenanceReader, StockProvenanceReaderAdapter>();

        // Inventory → Intake ACL (ADR-023 §6/A11): the pantry History grid's batched "Amend" eligibility
        // check (does this Purchase row have a committed line to amend), same join seam as
        // StockProvenanceReaderAdapter above but keyed by StockEntryId rather than the chip correlation.
        services.AddScoped<IAmendableLineReader, AmendableLineReaderAdapter>();

        // Housekeeping's 7 IProblemDetector registrations moved to Plantry.Web's Program.cs (ADR-024
        // Phase A, plantry-g3da.2): the detectors now live in Plantry.Web (the composition root) as
        // ADR-021 cross-schema read models, and this project must never reference Plantry.Web types.

        return services;
    }
}
