using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Plantry.Tests.Architecture;

/// <summary>
/// Guards the Plantry.Composition boundary (plantry-m1u). The composition assembly houses the
/// cross-context ACL adapters + the domain-event dispatch machinery, extracted out of Plantry.Web so
/// "how bounded contexts are wired together" is separate from "the web/UI host." The hard invariants:
///  1. No Composition type may depend on any Microsoft.AspNetCore.* type (the one ASP.NET-Identity-coupled
///     adapter reads through the Plantry.Identity.Application IHouseholdDirectory port instead of UserManager).
///  2. The composition assembly must not reference Plantry.Web (the dependency direction is Web → Composition),
///     nor any Microsoft.AspNetCore.* assembly, nor Plantry.Identity.Infrastructure (the ASP.NET-coupled port
///     impl stays in the host).
/// Checked at both the type level (NetArchTest) and the assembly-reference level (metadata) so the guard
/// holds regardless of the moved types' namespace strings.
/// </summary>
public sealed class CompositionBoundaryTests
{
    private static readonly Assembly CompositionAssembly =
        typeof(Plantry.Composition.CompositionServiceCollectionExtensions).Assembly;

    [Fact]
    public void Composition_Types_Should_Not_Depend_On_AspNetCore()
    {
        var result = Types.InAssembly(CompositionAssembly)
            .Should().NotHaveDependencyOnAny("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Plantry.Composition types depend on Microsoft.AspNetCore.* (the composition boundary must " +
            "stay free of ASP.NET types — route ASP.NET-coupled reads through an Identity application port):\n" +
            string.Join("\n", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Composition_Assembly_Should_Not_Reference_Web_Or_AspNetCore()
    {
        var forbidden = CompositionAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name =>
                name == "Plantry.Web"
                || name == "Plantry.Identity.Infrastructure"
                || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        Assert.True(forbidden.Count == 0,
            "Plantry.Composition references assemblies it must not (Plantry.Web / Plantry.Identity.Infrastructure / " +
            "Microsoft.AspNetCore.*) — the composition seam must not depend on the web host or ASP.NET Identity:\n" +
            string.Join("\n", forbidden));
    }
}
