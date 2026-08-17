namespace SlimDep.Core.Models;

/// <summary>A package that was detected as unused in a project.</summary>
public sealed record UnusedPackage(string Name, string Version, string Reason)
{
    /// <summary>Line number of the &lt;PackageReference&gt; element in the .csproj, if known.</summary>
    public int? LineNumber { get; init; }
}
