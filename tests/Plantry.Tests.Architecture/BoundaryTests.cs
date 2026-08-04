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
        "Plantry.Pantry",
        "Plantry.Market",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    // Pantry (Catalog + Inventory merged per ADR-024, plantry-g3da.6) is a Phase-1 downstream context —
    // every other context is a forbidden sibling.
    private static readonly string[] PantrySiblingContexts =
    [
        "Plantry.Identity",
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
    public void Pantry_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pantry.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pantry domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pantry_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pantry.Domain")
            .Should().NotHaveDependencyOnAny(PantrySiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pantry domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pantry_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pantry.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pantry application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pantry_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pantry.Application")
            .Should().NotHaveDependencyOnAny(PantrySiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pantry application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    // Market (Pricing + Deals merged per ADR-024) — every other context is a forbidden sibling. Deals'
    // former ACL soft-refs onto Catalog/Shopping/Inventory/Identity still hold; Pricing's own domain
    // referenced only SharedKernel before the merge and still does.
    private static readonly string[] MarketSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Pantry",
        "Plantry.Planning",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    private static readonly string[] IntakeSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Pantry",
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
        "Plantry.Pantry",
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
        "Plantry.Pantry",
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
    // (ADR-024 Phase A, plantry-g3da.2): the bounded context was dissolved — GetTidyUpPageQuery and the
    // detectors live directly in Plantry.Web (the composition root), which legitimately references
    // every context, so there is no boundary left to police here. Dismissal/HousekeepingDbContext moved
    // again (plantry-g3da.9, ADR-024 ratified option B) into Plantry.Composition.Infrastructure, the
    // read layer's persistence home — see DbContexts_Should_Reside_In_Infrastructure_Namespaces below.

    [Fact]
    public void DbContexts_Should_Reside_In_Infrastructure_Namespaces()
    {
        var result = Types.InCurrentDomain()
            .That().Inherit(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespaceMatching(@"^Plantry\.\w+\.Infrastructure")
            .GetResult();

        // ZERO exemptions (plantry-g3da.9, ADR-024 ratified option B): HousekeepingDbContext moved
        // from Plantry.Web.Housekeeping into Plantry.Composition.Infrastructure — the composition read
        // layer's standing persistence home — which satisfies this rule naturally, so the ad-hoc
        // exemption that stood during ADR-024 Phase A (plantry-g3da.2) is gone. This rule is back to
        // 10-for-10 with no carve-outs.
        Assert.True(result.IsSuccessful,
            "DbContext found outside Infrastructure namespace:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
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
    // (Plantry.Ai.Infrastructure, Plantry.SharedKernel, Plantry.Composition.Infrastructure) belong to
    // no context and are intentionally absent — they are the sanctioned homes for cross-cutting infra
    // concerns (plantry-g3da.9, ADR-024 ratified option B: Composition.Infrastructure is the second
    // sanctioned context-free infrastructure project, beside Ai.Infrastructure).
    private static readonly string[] InfrastructureContexts =
    [
        "Identity",
        "Pantry",
        "Market",
        "Planning",
        "Intake",
        "Recipes",
        // "Housekeeping" removed (ADR-024 Phase A, plantry-g3da.2): the bounded context was dissolved —
        // HousekeepingDbContext now lives in Plantry.Composition.Infrastructure (plantry-g3da.9), a
        // sanctioned context-free infrastructure project, not a per-context one.
    ];

    // Regression lock (plantry-ew5): no bounded context's *.Infrastructure assembly may reference
    // another bounded context's *.Infrastructure assembly. Catalog.Infrastructure and Inventory.Infrastructure
    // (now folded into Plantry.Pantry.Infrastructure, ADR-024 plantry-g3da.6), MealPlanning.Infrastructure
    // (now folded into Plantry.Planning.Infrastructure, ADR-024 plantry-g3da.5), and (the former)
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
