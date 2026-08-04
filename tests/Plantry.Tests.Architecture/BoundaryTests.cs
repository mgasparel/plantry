using NetArchTest.Rules;
using Xunit;

namespace Plantry.Tests.Architecture;

/// <summary>
/// Enforces dependency boundaries from PHASE-1-PLAN.md §Solution structure.
/// Runs as L1 (unit test layer) and fails the build on every PR.
///
/// Rules:
///  1. Domain/Application namespaces must not reference EF Core, ASP.NET Core, or Npgsql.
///  2. Domain namespaces must not reference sibling bounded contexts.
///  3. DbContext subclasses must live in *.Infrastructure namespaces.
///  4. Domain aggregates must not extend IdentityUser.
/// </summary>
public sealed class BoundaryTests
{
    private static readonly string[] InfraPackages =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
    ];

    private static readonly string[] SiblingContexts =
    [
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    private static readonly string[] CatalogSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Inventory",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    // Inventory must not reach into any sibling — Plantry.Catalog included. That exclusion is what
    // forces the unit-conversion + Catalog-read needs through the Port + Web-adapter seam (Slice 2).
    private static readonly string[] InventorySiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    [Fact]
    public void Identity_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Identity.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Identity domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Identity_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Identity.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Identity application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Identity_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Identity.Domain")
            .Should().NotHaveDependencyOnAny(SiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Identity domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Catalog_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Catalog.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Catalog domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Catalog_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Catalog.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Catalog application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Catalog_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Catalog.Domain")
            .Should().NotHaveDependencyOnAny(CatalogSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Catalog domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Inventory_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Inventory.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Inventory_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Inventory.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Inventory_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Inventory.Domain")
            .Should().NotHaveDependencyOnAny(InventorySiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Inventory_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Inventory.Application")
            .Should().NotHaveDependencyOnAny(InventorySiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    // Market (Pricing + Deals merged per ADR-024) — every other context is a forbidden sibling. Deals'
    // former ACL soft-refs onto Catalog/Shopping/Inventory/Identity still hold; Pricing's own domain
    // referenced only SharedKernel before the merge and still does.
    private static readonly string[] MarketSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    private static readonly string[] IntakeSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Recipes",
    ];

    // Recipes is a downstream consumer of every Phase-1 context but reaches them only through ports
    // (recipes-domain-model.md §1/§8) — its domain may reference only SharedKernel, so every other
    // context is a sibling it must not depend on directly.
    private static readonly string[] RecipesSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Intake",
    ];

    // Planning (MealPlanning + Shopping merged per ADR-024, plantry-g3da.5) is a Phase-3 downstream
    // context — its domain references only SharedKernel. It does not depend on Recipes or any Phase-1
    // context directly; it references recipe/product IDs as opaque Guids only (see DM-21 /
    // mealplanning.md / shopping.md).
    private static readonly string[] PlanningSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Market",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    [Fact]
    public void Market_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Market.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Market domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Market_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Market.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Market application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Market_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Market.Domain")
            .Should().NotHaveDependencyOnAny(MarketSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Market domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Market_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Market.Application")
            .Should().NotHaveDependencyOnAny(MarketSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Market application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Intake_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Intake.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Intake domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Intake_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Intake.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Intake application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Intake_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Intake.Domain")
            .Should().NotHaveDependencyOnAny(IntakeSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Intake domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Intake_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Intake.Application")
            .Should().NotHaveDependencyOnAny(IntakeSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Intake application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Recipes_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Recipes.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Recipes domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Recipes_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Recipes.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Recipes application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Recipes_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Recipes.Domain")
            .Should().NotHaveDependencyOnAny(RecipesSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Recipes domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Recipes_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Recipes.Application")
            .Should().NotHaveDependencyOnAny(RecipesSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Recipes application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Planning_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Planning.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Planning domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Planning_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Planning.Domain")
            .Should().NotHaveDependencyOnAny(PlanningSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Planning domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Planning_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Planning.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Planning application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Planning_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Planning.Application")
            .Should().NotHaveDependencyOnAny(PlanningSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Planning application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }


    // Housekeeping's boundary tests (Domain/Application sibling-context + infra checks) were removed
    // (ADR-024 Phase A, plantry-g3da.2): the bounded context was dissolved — Dismissal/GetTidyUpPageQuery
    // etc. now live directly in Plantry.Web (the composition root), which legitimately references every
    // context, so there is no boundary left to police here.

    [Fact]
    public void DbContexts_Should_Reside_In_Infrastructure_Namespaces()
    {
        var result = Types.InCurrentDomain()
            .That().Inherit(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespaceMatching(@"^Plantry\.\w+\.Infrastructure")
            .GetResult();

        // HousekeepingDbContext is a documented exemption (ADR-024 Phase A / plantry-g3da.2): the
        // Housekeeping bounded context was dissolved, and its DbContext now legitimately lives in
        // Plantry.Web.Housekeeping (the composition root) rather than a *.Infrastructure namespace —
        // this is a deliberate architectural decision, not a silent coincidental pass.
        var failingTypeNames = (result.FailingTypeNames ?? [])
            .Where(name => name != "Plantry.Web.Housekeeping.HousekeepingDbContext")
            .ToList();

        Assert.True(failingTypeNames.Count == 0,
            "DbContext found outside Infrastructure namespace:\n" +
            string.Join("\n", failingTypeNames));
    }

    [Fact]
    public void Domain_Types_Should_Not_Extend_IdentityUser()
    {
        var result = Types.InCurrentDomain()
            .That().ResideInNamespaceMatching(@"^Plantry\.\w+\.Domain")
            .Should().NotInherit(typeof(Microsoft.AspNetCore.Identity.IdentityUser))
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Domain type extends IdentityUser:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    // Every bounded context that has an Infrastructure assembly. Shared infra libraries
    // (Plantry.Ai.Infrastructure, Plantry.SharedKernel) belong to no context and are intentionally
    // absent — they are the sanctioned homes for cross-cutting infra concerns.
    private static readonly string[] InfrastructureContexts =
    [
        "Identity",
        "Catalog",
        "Inventory",
        "Market",
        "Planning",
        "Intake",
        "Recipes",
        // "Housekeeping" removed (ADR-024 Phase A, plantry-g3da.2): the bounded context was dissolved —
        // HousekeepingDbContext now lives in Plantry.Web, not a standalone *.Infrastructure project.
    ];

    // Regression lock (plantry-ew5): no bounded context's *.Infrastructure assembly may reference
    // another bounded context's *.Infrastructure assembly. MealPlanning.Infrastructure (now folded into
    // Plantry.Planning.Infrastructure, ADR-024 plantry-g3da.5) and (the former)
    // Deals.Infrastructure once referenced Plantry.Intake.Infrastructure solely to reuse the AiOptions /
    // AiTelemetry POCOs (Gate 2 violation); those primitives now live in the shared, context-free
    // Plantry.Ai.Infrastructure. This test fails the moment any such cross-context infra dependency is
    // reintroduced. Market.Infrastructure's reference to Plantry.Ai.Infrastructure (DealMatcher/FlyerSource,
    // carried over from Deals.Infrastructure) is allowed by construction — Ai.Infrastructure is not in the
    // context list, so it is never treated as a forbidden sibling target. The shared Plantry.Ai.Infrastructure
    // and Plantry.SharedKernel are exempt the same way.
    [Fact]
    public void Infrastructure_Should_Not_Reference_Sibling_Context_Infrastructure()
    {
        var failures = new List<string>();

        foreach (var context in InfrastructureContexts)
        {
            var ownNamespace = $"Plantry.{context}.Infrastructure";
            var siblingInfrastructureNamespaces = InfrastructureContexts
                .Where(other => other != context)
                .Select(other => $"Plantry.{other}.Infrastructure")
                .ToArray();

            var result = Types.InCurrentDomain()
                .That()
                .ResideInNamespace(ownNamespace)
                .Should().NotHaveDependencyOnAny(siblingInfrastructureNamespaces)
                .GetResult();

            if (!result.IsSuccessful)
                failures.Add($"{ownNamespace} → {string.Join(", ", result.FailingTypeNames ?? [])}");
        }

        Assert.True(failures.Count == 0,
            "Cross-context *.Infrastructure references found (plantry-ew5 boundary — move shared AI/infra " +
            "concerns to Plantry.Ai.Infrastructure or another context-free shared library instead):\n" +
            string.Join("\n", failures));
    }
}
