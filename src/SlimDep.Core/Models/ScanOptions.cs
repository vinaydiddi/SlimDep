namespace SlimDep.Core.Models;

/// <summary>Input options for a single project scan.</summary>
public sealed class ScanOptions
{
    public required string ProjectPath { get; init; }
    public bool IncludeTransitive { get; init; } = false;
    /// <summary>Packages suppressed inline via <c>--ignore</c> CLI flag (merged with .slimdepignore).</summary>
    public IReadOnlyCollection<string>? InlineIgnoredPackages { get; init; }
    public CancellationToken CancellationToken { get; init; } = default;
}
