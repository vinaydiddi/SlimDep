namespace SlimDep.Analysis;

/// <summary>
/// Built-in safe list: packages that do their job without the project's code ever importing
/// their namespaces (test adapters, coverage collectors, build/design-time tooling, hosting
/// startup assemblies). They would otherwise be reported as unused, and removing them breaks
/// test discovery, tooling or the build. Disabled with <c>--no-default-ignores</c>.
/// </summary>
internal static class DefaultIgnoreList
{
    // A trailing or leading '*' is a wildcard. Matching is case-insensitive.
    internal static readonly IReadOnlyList<string> Patterns =
    [
        // Test platform and adapters (discovered by the test host, never imported)
        "Microsoft.NET.Test.Sdk",
        "xunit.runner.*",
        "NUnit3TestAdapter",
        "NUnit.ConsoleRunner",
        "NUnit.Analyzers",
        "MSTest.TestAdapter",
        "Microsoft.Testing.Platform.MSBuild",
        "Microsoft.Testing.Extensions.*",
        "JunitXml.TestLogger",
        "XunitXml.TestLogger",
        "NunitXml.TestLogger",
        "GitHubActionsTestLogger",

        // Code coverage
        "coverlet.*",
        "Microsoft.CodeCoverage",

        // Source link, versioning and packaging
        "Microsoft.SourceLink.*",
        "MinVer",
        "Nerdbank.GitVersioning",
        "GitVersion.MsBuild",
        "Microsoft.Build.Tasks.Git",

        // Design-time / CLI tooling (dotnet ef, scaffolding, containers)
        "*.Design",
        "Microsoft.EntityFrameworkCore.Tools",
        "Microsoft.VisualStudio.Web.CodeGeneration.*",
        "Microsoft.VisualStudio.Azure.Containers.Tools.Targets",
        "Microsoft.NET.Build.Containers",

        // Front-end build integration and hosting startup assemblies
        "Microsoft.TypeScript.MSBuild",
        "Microsoft.Web.LibraryManager.Build",
        "Microsoft.AspNetCore.SpaProxy",

        // Native providers loaded by reflection at runtime (Microsoft.Data.Sqlite calls Batteries_V2.Init)
        "SQLitePCLRaw.bundle_*",
        "SQLitePCLRaw.provider.*",

        // Analyzers
        "*.Analyzers",
        "Microsoft.CodeAnalysis.NetAnalyzers",
        "Microsoft.VisualStudio.Threading.Analyzers",
    ];

    /// <summary>Returns the matching pattern, or <c>null</c> if the package is not on the list.</summary>
    internal static string? Match(string packageName)
    {
        foreach (string pattern in Patterns)
        {
            if (Matches(pattern, packageName))
                return pattern;
        }
        return null;
    }

    internal static bool Matches(string pattern, string name)
    {
        if (pattern.StartsWith('*'))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*'))
            return name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
