namespace SlimDep.Core.Models;

/// <summary>The output of analyzing a single project.</summary>
public sealed class ScanResult
{
    public required string ProjectPath { get; init; }
    public required IReadOnlyList<UnusedPackage> UnusedPackages { get; init; }
    public required int TotalDirectPackages { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>SLIMDEP002: transitive packages the code uses directly without referencing them.</summary>
    public IReadOnlyList<UndeclaredPackage> UndeclaredPackages { get; init; } = [];

    /// <summary>Unused-looking direct references kept because they pin a higher version.</summary>
    public IReadOnlyList<PinnedPackage> PinnedPackages { get; init; } = [];

    /// <summary>Unused-looking direct references not reported because of the default safe list.</summary>
    public IReadOnlyList<SkippedPackage> SkippedPackages { get; init; } = [];

    public bool HasUnused => UnusedPackages.Count > 0;

    public bool HasUndeclared => UndeclaredPackages.Count > 0;
}
