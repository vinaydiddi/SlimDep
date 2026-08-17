namespace SlimDep.Analysis;

/// <summary>Holds the namespaces exported by a single NuGet package.</summary>
internal sealed class PackageNamespaceInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required IReadOnlySet<string> Namespaces { get; init; }
}
