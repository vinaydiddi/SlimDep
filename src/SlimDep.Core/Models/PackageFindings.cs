namespace SlimDep.Core.Models;

/// <summary>
/// SLIMDEP002 — a transitive package whose namespaces the project's code uses directly,
/// without a <c>&lt;PackageReference&gt;</c> of its own. If the package that brings it in
/// is upgraded or removed, it can silently disappear and break the build.
/// </summary>
/// <param name="Name">Package id.</param>
/// <param name="Version">Resolved version.</param>
/// <param name="IntroducedBy">Direct package(s) that currently bring it in.</param>
/// <param name="UsedNamespaces">Namespaces from this package that the code references.</param>
public sealed record UndeclaredPackage(
    string Name,
    string Version,
    IReadOnlyList<string> IntroducedBy,
    IReadOnlyList<string> UsedNamespaces)
{
    public string Reason =>
        $"Code uses {string.Join(", ", UsedNamespaces)} but the package is only referenced transitively"
        + (IntroducedBy.Count > 0 ? $" (via {string.Join(", ", IntroducedBy)})" : string.Empty)
        + ". Add an explicit <PackageReference>.";
}

/// <summary>
/// A direct reference whose namespaces are unused, kept because it raises the version of a
/// package that other dependencies would otherwise resolve lower (typically a security pin).
/// Removing it would downgrade the package to <see cref="TransitiveVersion"/>.
/// </summary>
/// <param name="Name">Package id.</param>
/// <param name="Version">Version pinned by the direct reference.</param>
/// <param name="TransitiveVersion">Version that would be resolved without the pin.</param>
/// <param name="RequiredBy">Packages (or "project X") that depend on it at the lower version.</param>
/// <param name="TargetFrameworks">
/// Target frameworks where the reference acts as a pin, when known. For multi-targeted projects
/// a reference can be a pin on one TFM and redundant on another; it is kept if it pins any.
/// </param>
public sealed record PinnedPackage(
    string Name,
    string Version,
    string TransitiveVersion,
    IReadOnlyList<string> RequiredBy,
    IReadOnlyList<string>? TargetFrameworks = null)
{
    public string Reason =>
        "Version pin"
        + (TargetFrameworks is { Count: > 0 } ? $" on {string.Join(", ", TargetFrameworks)}" : string.Empty)
        + $": overrides {TransitiveVersion} required by {string.Join(", ", RequiredBy)}. "
        + $"Removing it would downgrade {Name} to {TransitiveVersion}.";
}

/// <summary>
/// A direct reference that looked unused but was not reported because it is on the built-in
/// safe list or is marked <c>PrivateAssets="all"</c> (build-time tooling).
/// </summary>
public sealed record SkippedPackage(string Name, string Version, string Reason);
