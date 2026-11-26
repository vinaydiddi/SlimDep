namespace SlimDep.Analysis;

/// <summary>Holds the namespaces exported by a single NuGet package.</summary>
internal sealed class PackageNamespaceInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required IReadOnlySet<string> Namespaces { get; init; }

    /// <summary><c>true</c> for a direct <c>&lt;PackageReference&gt;</c>; <c>false</c> for a transitive dependency.</summary>
    public bool IsDirect { get; init; } = true;

    /// <summary>For transitive packages: the direct package(s) that pull this one in.</summary>
    public IReadOnlyList<string> IntroducedBy { get; init; } = [];
}
