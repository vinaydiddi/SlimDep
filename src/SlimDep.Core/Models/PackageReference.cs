namespace SlimDep.Core.Models;

/// <summary>Identifies a NuGet package referenced by a project.</summary>
public sealed record PackageReference(string Name, string Version)
{
    public bool IsDirectReference { get; init; } = true;
}
