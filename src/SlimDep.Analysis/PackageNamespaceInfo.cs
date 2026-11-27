namespace SlimDep.Analysis;

/// <summary>Holds the namespaces exported by a single NuGet package, plus its place in the dependency graph.</summary>
internal sealed class PackageNamespaceInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required IReadOnlySet<string> Namespaces { get; init; }

    /// <summary><c>true</c> for a direct <c>&lt;PackageReference&gt;</c>; <c>false</c> for a transitive dependency.</summary>
    public bool IsDirect { get; init; } = true;

    /// <summary>For transitive packages: the direct package(s) or "project X" references that pull this one in.</summary>
    public IReadOnlyList<string> IntroducedBy { get; init; } = [];

    /// <summary>
    /// Per target framework: the resolved version and the packages (or "project X") that depend
    /// on this one with the minimum version each asks for. Kept per target because dependencies
    /// often differ by TFM — a reference can be redundant on net8.0 yet a version pin on net48.
    /// </summary>
    public IReadOnlyList<TargetDependencies> Targets { get; init; } = [];

    /// <summary>The package has no compile/runtime assemblies — a metapackage or tooling-only package.</summary>
    public bool HasNoAssemblies { get; init; }

    /// <summary>Direct reference marked <c>PrivateAssets="all"</c> (development-only dependency).</summary>
    public bool PrivateAssetsAll { get; init; }
}

/// <summary>A dependency edge: <paramref name="Dependent"/> requires at least <paramref name="MinVersion"/>.</summary>
internal sealed record DependencyRequirement(string Dependent, string MinVersion);

/// <summary>How a package resolves within one target framework.</summary>
internal sealed record TargetDependencies(
    string TargetFramework,
    string ResolvedVersion,
    IReadOnlyList<DependencyRequirement> RequiredBy);

/// <summary>Everything SlimDep reads from <c>project.assets.json</c>.</summary>
internal sealed class AssetsInfo
{
    /// <summary>All direct packages and every transitive package that has assemblies.</summary>
    public required IReadOnlyList<PackageNamespaceInfo> Packages { get; init; }

    /// <summary>Shared frameworks referenced by the project, e.g. <c>Microsoft.AspNetCore.App</c>.</summary>
    public required IReadOnlyList<string> FrameworkReferences { get; init; }

    /// <summary>Target frameworks of the restore graph (RID suffixes removed), e.g. <c>net8.0</c>, <c>net48</c>.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];
}
