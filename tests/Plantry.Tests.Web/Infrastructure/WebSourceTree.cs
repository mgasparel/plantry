namespace Plantry.Tests.Web.Infrastructure;

/// <summary>
/// Shared repo-walk helpers for source-scanning guard tests over <c>src/Plantry.Web</c>
/// (plantry-sz4b — consolidates the previously duplicated <c>RepoRoot</c> / <c>EnumerateSourceFiles</c>
/// copies in <see cref="Plantry.Tests.Web.Formatting.MoneyFormattingGuardTests"/> and
/// <see cref="Plantry.Tests.Web.Conventions.AlpineXDataRawGuardTests"/>).
/// </summary>
internal static class WebSourceTree
{
    public static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            // wwwroot holds vendored JS/CSS and the client-side islands (their money formatting/Alpine
            // splicing is a separate concern, plantry-2x6e.3); obj and bin hold generated build artifacts.
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(WebSourceTree).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Plantry.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Plantry.sln).");
    }
}
