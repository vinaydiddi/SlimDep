namespace SlimDep.Core.Models;

/// <summary>A package that was detected as unused in a project.</summary>
public sealed record UnusedPackage(string Name, string Version, string Reason)
{
    /// <summary>Line number of the &lt;PackageReference&gt; element in the .csproj, if known.</summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// <c>true</c> when the package is a transitive (indirect) dependency rather than a
    /// direct <c>&lt;PackageReference&gt;</c>. Only reported with <c>--include-transitive</c>.
    /// Transitive packages cannot be removed from the project file.
    /// </summary>
    public bool IsTransitive { get; init; }
}
