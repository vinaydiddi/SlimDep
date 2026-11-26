namespace SlimDep.Core.Models;

/// <summary>Input options for a single project scan.</summary>
public sealed class ScanOptions
{
    public required string ProjectPath { get; init; }
    public bool IncludeTransitive { get; init; } = false;
    /// <summary>Packages suppressed inline via <c>--ignore</c> CLI flag (merged with .slimdepignore).</summary>
    public IReadOnlyCollection<string>? InlineIgnoredPackages { get; init; }
    /// <summary>
    /// When <c>true</c> (default), packages on the built-in safe list (test adapters, coverage
    /// collectors, SourceLink, design-time tooling…) and <c>PrivateAssets="all"</c> references
    /// are never reported as unused. Disabled by <c>--no-default-ignores</c>.
    /// </summary>
    public bool UseDefaultIgnores { get; init; } = true;
    public CancellationToken CancellationToken { get; init; } = default;
}
