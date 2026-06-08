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
    ];

    private static readonly string[] CatalogSiblingContexts =
    [
        "Plantry.Identity",
        "Plantry.Inventory",
        "Plantry.Pricing",
        "Plantry.Shopping",
        "Plantry.Intake",
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
