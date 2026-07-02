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
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
    ];

    private static readonly string[] CatalogSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
    ];

    // Inventory must not reach into any sibling — Plantry.Catalog included. That exclusion is what
    // forces the unit-conversion + Catalog-read needs through the Port + Web-adapter seam (Slice 2).
    private static readonly string[] InventorySiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
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

    private static readonly string[] PricingSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
    ];

    private static readonly string[] IntakeSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
    ];

    // Recipes is a downstream consumer of every Phase-1 context but reaches them only through ports
    // (recipes-domain-model.md §1/§8) — its domain may reference only SharedKernel, so every other
    // context is a sibling it must not depend on directly.
    private static readonly string[] RecipesSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.MealPlanning",
    ];

    // MealPlanning is a Phase-3 downstream context — its domain references only SharedKernel.
    // It does not depend on Recipes or any Phase-1 context directly; it references recipe/product
    // IDs as opaque Guids only (see DM-21 / mealplanning.md).
    private static readonly string[] MealPlanningSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
    ];

    [Fact]
    public void Pricing_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pricing.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pricing domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pricing_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pricing.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pricing application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pricing_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pricing.Domain")
            .Should().NotHaveDependencyOnAny(PricingSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pricing domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Pricing_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Pricing.Application")
            .Should().NotHaveDependencyOnAny(PricingSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Pricing application references sibling contexts:\n" +
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
    public void MealPlanning_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.MealPlanning.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "MealPlanning domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void MealPlanning_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.MealPlanning.Domain")
            .Should().NotHaveDependencyOnAny(MealPlanningSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "MealPlanning domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void MealPlanning_Application_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.MealPlanning.Application")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "MealPlanning application references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void MealPlanning_Application_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.MealPlanning.Application")
            .Should().NotHaveDependencyOnAny(MealPlanningSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "MealPlanning application references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    // Deals is a Phase-5 core context wrapping an untrusted flyer feed behind an ACL. Its domain
    // references only SharedKernel; it reaches Catalog/Pricing/Shopping/Inventory/Identity by opaque
    // Guid soft-refs only (deals-domain-model.md §1/§8), so every other context is a sibling it must
    // not depend on directly.
    private static readonly string[] DealsSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Catalog",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
        "Plantry.Recipes",
        "Plantry.MealPlanning",
    ];

    [Fact]
    public void Deals_Domain_Should_Not_Reference_Infrastructure_Packages()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Deals.Domain")
            .Should().NotHaveDependencyOnAny(InfraPackages)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Deals domain references infrastructure packages:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Deals_Domain_Should_Not_Reference_Sibling_Contexts()
    {
        var result = Types.InCurrentDomain()
            .That()
            .ResideInNamespace("Plantry.Deals.Domain")
            .Should().NotHaveDependencyOnAny(DealsSiblingContexts)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Deals domain references sibling contexts:\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void DbContexts_Should_Reside_In_Infrastructure_Namespaces()
    {
        var result = Types.InCurrentDomain()
            .That().Inherit(typeof(Microsoft.EntityFrameworkCore.DbContext))
            .Should().ResideInNamespaceMatching(@"^Plantry\.\w+\.Infrastructure")
            .GetResult();

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
}
