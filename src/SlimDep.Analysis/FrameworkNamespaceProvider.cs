using System.Collections.Concurrent;

namespace SlimDep.Analysis;

/// <summary>
/// Supplies the public namespaces of the installed .NET shared frameworks
/// (<c>Microsoft.NETCore.App</c>, <c>Microsoft.AspNetCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>).
/// </summary>
/// <remarks>
/// Packages such as <c>System.Text.Json</c> or <c>Microsoft.Extensions.Logging.Abstractions</c>
/// often appear transitively while the same namespaces are also provided by the framework.
/// Code using those namespaces does not depend on the package, so SLIMDEP002 must ignore them.
/// The newest installed version of each framework is used; namespace sets are stable across
/// versions and a slightly larger set only makes SLIMDEP002 more conservative.
/// </remarks>
internal static class FrameworkNamespaceProvider
{
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal const string NetCoreApp = "Microsoft.NETCore.App";

    /// <summary>
    /// Returns the union of namespaces for <c>Microsoft.NETCore.App</c> plus every framework in
    /// <paramref name="frameworkReferences"/> (e.g. <c>Microsoft.AspNetCore.App</c>).
    /// </summary>
    internal static HashSet<string> GetNamespaces(IEnumerable<string> frameworkReferences)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fw in frameworkReferences.Append(NetCoreApp).Distinct(StringComparer.OrdinalIgnoreCase))
            result.UnionWith(Cache.GetOrAdd(fw, Load));
        return result;
    }

    private static IReadOnlySet<string> Load(string frameworkName)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        string? sharedRoot = FindSharedRoot();
        if (sharedRoot is null) return namespaces;

        string fwDir = Path.Combine(sharedRoot, frameworkName);
        if (!Directory.Exists(fwDir)) return namespaces;

        string? newest = Directory.EnumerateDirectories(fwDir)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v))
            .OrderByDescending(v => v!, Comparer<string>.Create(PackageVersion.Compare))
            .FirstOrDefault();
        if (newest is null) return namespaces;

        foreach (string dll in Directory.EnumerateFiles(Path.Combine(fwDir, newest), "*.dll"))
            namespaces.UnionWith(AssetsFileParser.ExtractNamespaces(dll));

        return namespaces;
    }

    /// <summary>Finds <c>&lt;dotnet root&gt;/shared</c> from the runtime this tool is running on.</summary>
    private static string? FindSharedRoot()
    {
        // typeof(object) lives in <root>/shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
        string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        string? shared = runtimeDir is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir));
        if (shared is not null && Directory.Exists(shared)) return shared;

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            string candidate = Path.Combine(dotnetRoot, "shared");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
