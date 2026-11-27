using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace SlimDep.Analysis;

/// <summary>
/// Supplies the namespaces a project's target frameworks provide without any package.
/// </summary>
/// <remarks>
/// Packages such as <c>System.Text.Json</c> or <c>Microsoft.Extensions.Logging.Abstractions</c>
/// often appear transitively while the same namespaces are also provided by the framework.
/// Code using those namespaces does not depend on the package, so SLIMDEP002 must ignore them —
/// but only where the framework really provides them: <c>System.Text.Json</c> ships with .NET 8,
/// not with .NET Framework 4.8.
/// <list type="bullet">
/// <item>.NET Core / .NET 5+ targets: the installed shared frameworks (<c>Microsoft.NETCore.App</c>
/// plus referenced ones such as <c>Microsoft.AspNetCore.App</c>), newest installed version.</item>
/// <item>.NET Framework and .NET Standard targets: the .NET Standard API surface, read from the
/// <c>netstandard.dll</c> facade. This approximates what every such target provides.</item>
/// </list>
/// For multi-targeted projects a namespace counts as framework-provided only if every target provides it.
/// </remarks>
internal static class FrameworkNamespaceProvider
{
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal const string NetCoreApp = "Microsoft.NETCore.App";
    private const string NetStandardKey = "<netstandard>";

    // net5.0, net8.0, net10.0-windows … (net48 / net472 have no dot and are .NET Framework)
    private static readonly Regex ModernNetTfm = new(@"^net(\d+)\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the namespaces provided by every one of <paramref name="targetFrameworks"/>.
    /// With no target frameworks known, .NET Core is assumed.
    /// </summary>
    internal static HashSet<string> GetNamespaces(IEnumerable<string> frameworkReferences, IEnumerable<string> targetFrameworks)
    {
        var refs = frameworkReferences.ToList();
        var tfms = targetFrameworks.ToList();
        if (tfms.Count == 0) tfms.Add("net8.0");

        HashSet<string>? result = null;
        foreach (bool isNetCore in tfms.Select(IsNetCore).Distinct())
        {
            var provided = isNetCore ? SharedFrameworkNamespaces(refs) : new HashSet<string>(Cache.GetOrAdd(NetStandardKey, _ => LoadNetStandard()), StringComparer.Ordinal);
            if (result is null) result = provided;
            else result.IntersectWith(provided);
        }
        return result ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>True for .NET Core and .NET 5+ target frameworks (short or long form).</summary>
    internal static bool IsNetCore(string tfm)
    {
        if (tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase)) return true;
        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)) return true;
        var m = ModernNetTfm.Match(tfm);
        return m.Success && int.TryParse(m.Groups[1].Value, out int major) && major >= 5;
    }

    private static HashSet<string> SharedFrameworkNamespaces(IEnumerable<string> frameworkReferences)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fw in frameworkReferences.Append(NetCoreApp).Distinct(StringComparer.OrdinalIgnoreCase))
            result.UnionWith(Cache.GetOrAdd(fw, LoadSharedFramework));
        return result;
    }

    private static IReadOnlySet<string> LoadSharedFramework(string frameworkName)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        string? dir = NewestFrameworkDirectory(frameworkName);
        if (dir is null) return namespaces;

        foreach (string dll in Directory.EnumerateFiles(dir, "*.dll"))
            namespaces.UnionWith(AssetsFileParser.ExtractNamespaces(dll));

        return namespaces;
    }

    /// <summary>
    /// The netstandard.dll shipped with the shared framework is a facade: its API surface is a
    /// list of type forwards, so namespaces are read from exported types as well as definitions.
    /// </summary>
    private static IReadOnlySet<string> LoadNetStandard()
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        string? dir = NewestFrameworkDirectory(NetCoreApp);
        string? facade = dir is null ? null : Path.Combine(dir, "netstandard.dll");
        if (facade is null || !File.Exists(facade)) return namespaces;

        try
        {
            using var stream = File.OpenRead(facade);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return namespaces;
            var md = pe.GetMetadataReader();

            foreach (var handle in md.ExportedTypes)
            {
                string ns = md.GetString(md.GetExportedType(handle).Namespace);
                if (!string.IsNullOrEmpty(ns)) namespaces.Add(ns);
            }
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                if (!def.Attributes.HasFlag(System.Reflection.TypeAttributes.Public)) continue;
                string ns = md.GetString(def.Namespace);
                if (!string.IsNullOrEmpty(ns)) namespaces.Add(ns);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // fall through with whatever was read
        }

        return namespaces;
    }

    private static string? NewestFrameworkDirectory(string frameworkName)
    {
        string? sharedRoot = FindSharedRoot();
        if (sharedRoot is null) return null;

        string fwDir = Path.Combine(sharedRoot, frameworkName);
        if (!Directory.Exists(fwDir)) return null;

        string? newest = Directory.EnumerateDirectories(fwDir)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v))
            .OrderByDescending(v => v!, Comparer<string>.Create(PackageVersion.Compare))
            .FirstOrDefault();

        return newest is null ? null : Path.Combine(fwDir, newest);
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
