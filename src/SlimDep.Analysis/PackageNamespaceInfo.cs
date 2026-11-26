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
    /// Packages (or "project X") that depend on this one, each with the minimum version it asks for.
    /// Used to detect direct references that pin a higher version than the graph would resolve.
    /// </summary>
    public IReadOnlyList<DependencyRequirement> RequiredBy { get; init; } = [];

    /// <summary>The package has no compile/runtime assemblies — a metapackage or tooling-only package.</summary>
    public bool HasNoAssemblies { get; init; }

    /// <summary>Direct reference marked <c>PrivateAssets="all"</c> (development-only dependency).</summary>
    public bool PrivateAssetsAll { get; init; }
}

/// <summary>A dependency edge: <paramref name="Dependent"/> requires at least <paramref name="MinVersion"/>.</summary>
internal sealed record DependencyRequirement(string Dependent, string MinVersion);

/// <summary>Everything SlimDep reads from <c>project.assets.json</c>.</summary>
internal sealed class AssetsInfo
{
    /// <summary>All direct packages and every transitive package that has assemblies.</summary>
    public required IReadOnlyList<PackageNamespaceInfo> Packages { get; init; }

    /// <summary>Shared frameworks referenced by the project, e.g. <c>Microsoft.AspNetCore.App</c>.</summary>
    public required IReadOnlyList<string> FrameworkReferences { get; init; }
}
