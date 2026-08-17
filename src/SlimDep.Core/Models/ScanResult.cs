namespace SlimDep.Core.Models;

/// <summary>The output of analyzing a single project.</summary>
public sealed class ScanResult
{
    public required string ProjectPath { get; init; }
    public required IReadOnlyList<UnusedPackage> UnusedPackages { get; init; }
    public required int TotalDirectPackages { get; init; }
    public required TimeSpan Duration { get; init; }

    public bool HasUnused => UnusedPackages.Count > 0;
}
